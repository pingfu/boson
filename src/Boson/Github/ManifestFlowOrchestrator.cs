using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Boson.Caddy;
using Boson.Deploy;
using Boson.Platform;
using Boson.Serve;
using Boson.Storage;
using Boson.Util;
using Microsoft.Extensions.Logging;

namespace Boson.Github;

public sealed class BosonValidationException(string message) : Exception(message);

public enum SetupPhase
{
    AwaitingManifest,
    AwaitingInstall,
    Finalizing,
    Fetching,
    Done,
    FetchFailed,

    /// <summary>The project is saved and declaring its deployments threw.</summary>
    DeclareFailed,
    Failed,
}

/// <summary>
/// How declaring a project's deployments ended. <see cref="Error"/> is the
/// reason it stopped before creating any, in words the operator acts on.
/// </summary>
public sealed record Declaration(
    string? Error, IReadOnlyList<string> Warnings, IReadOnlyList<string> EnvSets);

/// <summary>
/// Owns the App-setup flow inside the daemon: pending entries are in-memory,
/// keyed by a one-time state token with a 15-minute TTL. In-memory is what
/// makes abandonment free: a browser session that never finishes, or a daemon
/// restart, evaporates the entry with nothing to clean up.
///
/// Nothing is persisted until the flow has everything, and the project row is
/// inserted whole, because GitHub issues the PEM and webhook secret exactly
/// once. A half-written row would hold credentials that cannot be re-fetched
/// and cannot be completed.
/// </summary>
public sealed class ManifestFlowOrchestrator(
    IProjectsRepository projects,
    IDeploymentsRepository deployments,
    IPlatformRepository platform,
    ICaddySynchroniser caddy,
    IGitCli git,
    IInstallationTokenMinter minter,
    IGithubClient github,
    IDnsResolver dns,
    BosonPaths paths,
    TimeProvider clock,
    ILogger logger)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private sealed class PendingSetup
    {
        public required string Token { get; init; }
        public required string Repo { get; init; }

        /// <summary>Whether the repository's owner is an organisation, which decides where the App is created.</summary>
        public required bool Organisation { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public List<string> Warnings { get; } = [];

        /// <summary>What the clone turned out to need, for the CLI to name as files to create.</summary>
        public List<string> EnvSets { get; } = [];
        public SetupPhase Phase { get; set; } = SetupPhase.AwaitingManifest;
        public ManifestConversion? Credentials { get; set; }
        public string? Error { get; set; }
        public Task? Finalisation { get; set; }
    }

    private readonly Dictionary<string, PendingSetup> _pending = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _declaring = new(1, 1);

    // By repository, latest run only. In-memory like the setups: a restart
    // forgets a run, and the next `boson deploy` starts another.
    private readonly Dictionary<string, Task<Declaration>> _declarations = new();

    public async Task<AddStartResponse> BeginAddAsync(AddRequest request, CancellationToken ct = default)
    {
        var admin = platform.Get(PlatformRepository.AdminHostname)
            ?? throw new BosonValidationException("platform not initialised; run boson init first");

        if (!RepoName.TryCanonicalise(request.Repo, out var repo))
            throw new BosonValidationException($"invalid repo (expected org/name): {request.Repo}");

        if (projects.GetByRepo(repo) is not null)
            throw new BosonValidationException($"repo already added: {repo}");

        var (owner, _) = RepoName.Split(repo);

        var organisation = await OwnerIsOrganisationAsync(owner, request.Owner, ct);

        // Hostnames come from `_boson.yml`, which lives in the repository, which
        // needs the App that this flow is about to create. So nothing about
        // what gets served is known yet, and the checks on it happen after the
        // clone rather than here.
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var entry = new PendingSetup
        {
            Token = token,
            Repo = repo,
            Organisation = organisation,
            CreatedAt = clock.GetUtcNow(),
        };

        lock (_gate)
        {
            Sweep();
            _pending[token] = entry;
        }

        // GitHub creates a personal App under whoever is signed in, and boson
        // cannot see who that is. Signed in as anyone else, the App installs
        // on the wrong account and the first fetch is what finds out.
        string[] warnings = organisation
            ? []
            :
            [
                $"{owner} is a personal account, so the App is created under whichever account is " +
                $"signed in to GitHub in your browser. It can read {repo} when that account is {owner}",
            ];

        return new AddStartResponse(
            $"https://{admin}/_boson/setup-app/start?state={token}", token, warnings);
    }

    /// <summary>
    /// A private App installs on the account that owns it, so an App for an
    /// organisation's repository has to be created under the organisation, at
    /// a different address from a personal one. The operator's word is taken
    /// over a lookup, which is how an add proceeds when GitHub cannot be asked.
    /// </summary>
    private async Task<bool> OwnerIsOrganisationAsync(string owner, string? declared, CancellationToken ct)
    {
        switch (declared)
        {
            case "org": return true;
            case "user": return false;
            case not null:
                throw new BosonValidationException($"invalid --owner (expected org or user): {declared}");
        }

        try
        {
            return await github.IsOrganisationAsync(owner, ct);
        }
        catch (GithubApiException e) when (e.Status == 404)
        {
            throw new BosonValidationException($"GitHub has no account named {owner}");
        }
        catch (Exception e) when (e is GithubApiException or HttpRequestException or TaskCanceledException)
        {
            throw new BosonValidationException(
                $"could not ask GitHub whether {owner} is an organisation ({e.Message}); " +
                "re-run with --owner org or --owner user");
        }
    }

    public string? RenderStartPage(string state)
    {
        var entry = Lookup(state);
        if (entry is null) return null;
        var admin = platform.Get(PlatformRepository.AdminHostname)!;
        var (owner, name) = RepoName.Split(entry.Repo);

        var manifest = new JsonObject
        {
            ["name"] = $"boson-{name}",
            ["url"] = $"https://github.com/{entry.Repo}",
            ["redirect_url"] = $"https://{admin}/_boson/setup-app/callback",
            ["setup_url"] = $"https://{admin}/_boson/setup-app/installed",
            // The App must carry a webhook address at creation, and the one
            // deliveries need is in a repository this App does not yet exist to
            // read. The admin name holds the place until the clone names the
            // hostname, and boson moves it with the App's own token.
            ["hook_attributes"] = new JsonObject
            {
                ["url"] = $"https://{admin}{CaddyConfigBuilder.WebhookPathPrefix}{entry.Repo}",
            },
            ["public"] = false,
            ["default_events"] = new JsonArray("push"),
            ["default_permissions"] = new JsonObject
            {
                ["contents"] = "read",
                ["metadata"] = "read",
            },
        };

        return EmbeddedResources.ManifestStartHtml
            .Replace("{{APP_NEW_URL}}", entry.Organisation
                ? $"https://github.com/organizations/{owner}/settings/apps/new"
                : "https://github.com/settings/apps/new")
            .Replace("{{MANIFEST_JSON}}", manifest.ToJsonString())
            .Replace("{{STATE}}", entry.Token);
    }

    /// <summary>Returns the installations/new redirect URL, or null for an unknown state.</summary>
    public async Task<string?> HandleCallbackAsync(string state, string code, CancellationToken ct = default)
    {
        var entry = Lookup(state);
        if (entry is null || entry.Phase != SetupPhase.AwaitingManifest) return null;

        try
        {
            entry.Credentials = await github.ConvertManifestAsync(code, ct);
        }
        catch (Exception e)
        {
            entry.Phase = SetupPhase.Failed;
            entry.Error = e.Message;
            logger.LogError(e, "manifest conversion failed for {Repo}", entry.Repo);
            return null;
        }

        entry.Phase = SetupPhase.AwaitingInstall;
        return $"https://github.com/apps/{entry.Credentials.Slug}/installations/new?state={entry.Token}";
    }

    /// <summary>Captures the installation id and finalises in the background.</summary>
    public bool HandleInstalled(string state, long installationId)
    {
        var entry = Lookup(state);
        if (entry is null || entry.Credentials is null) return false;

        // GitHub can land on setup_url more than once for one install (a browser
        // refresh, or the operator revisiting the URL). Only the first call
        // starts finalisation; a repeat still renders the success page, because
        // the install did happen and a 404 would read as though it had not.
        // A failed entry is the one repeat that must 404.
        if (entry.Phase != SetupPhase.AwaitingInstall) return entry.Phase != SetupPhase.Failed;

        // Finalisation runs detached: this call is servicing GitHub's browser
        // redirect, and the row insert, Caddy push and initial fetch together
        // take longer than a page load should wait. The CLI polls GetStatus.
        entry.Phase = SetupPhase.Finalizing;
        entry.Finalisation = Task.Run(() => FinaliseAsync(entry, installationId));
        return true;
    }

    private async Task FinaliseAsync(PendingSetup entry, long installationId)
    {
        // What a failure leaves behind depends on which side of the insert it
        // fell, and the CLI tells the operator a different thing for each.
        var saved = false;

        try
        {
            var credentials = entry.Credentials!;

            var project = new Project
            {
                Repo = entry.Repo,
                GithubAppId = credentials.Id,
                GithubAppSlug = credentials.Slug,
                GithubInstallationId = installationId,
                GithubWebhookSecret = credentials.WebhookSecret,
                GithubAppPem = credentials.Pem,
                GithubAppOrg = entry.Organisation ? RepoName.Split(entry.Repo).Org : null,
            };

            project = project with { Id = projects.Insert(project) };

            saved = true;

            entry.Phase = SetupPhase.Fetching;

            var declaration = await DeclareAsync(project);

            entry.Warnings.AddRange(declaration.Warnings);
            entry.EnvSets.AddRange(declaration.EnvSets);

            if (declaration.Error is not null)
            {
                // The App and its keys are kept: GitHub issues them once, and
                // `boson deploy` runs the declaration again.
                entry.Phase = SetupPhase.FetchFailed;
                entry.Error = declaration.Error;
                return;
            }

            entry.Phase = SetupPhase.Done;
            logger.LogInformation("{Repo}: project added", entry.Repo);
        }
        catch (Exception e)
        {
            entry.Phase = saved ? SetupPhase.DeclareFailed : SetupPhase.Failed;
            entry.Error = e.Message;
            logger.LogError(e, "add flow failed for {Repo}", entry.Repo);
        }
    }

    /// <summary>
    /// Starts <see cref="DeclareAsync"/> for `boson deploy` and returns at
    /// once: the clone takes as long as the repository is large, and the CLI
    /// follows it through <see cref="GetDeclareStatus"/> the way `boson add`
    /// follows a setup. A second call while one is running joins it.
    /// </summary>
    public void BeginDeclare(Project project)
    {
        lock (_gate)
        {
            if (_declarations.TryGetValue(project.Repo, out var running) && !running.IsCompleted) return;

            // Detached from the request that asked: Ctrl-C in the CLI stops
            // the display, and a half-made clone is worth less than a whole one.
            _declarations[project.Repo] = Task.Run(() => DeclareForDeployAsync(project));
        }
    }

    private async Task<Declaration> DeclareForDeployAsync(Project project)
    {
        Declaration declaration;

        try
        {
            declaration = await DeclareAsync(project);
        }
        catch (Exception e)
        {
            logger.LogError(e, "{Repo}: declaring deployments failed", project.Repo);

            return new Declaration(e.Message, [], []);
        }

        // `boson add` finishes on a file of patterns; a deploy has nothing to
        // deploy, and the warnings say what that leaves undone.
        if (declaration.Error is null && deployments.ListActiveForRepo(project.Repo).Count == 0)
            return declaration with
            {
                Error = $"no deployments for {project.Repo}; {BosonFile.FileName} names no branch " +
                        "outright, and a pattern's deployments are created by pushes",
            };

        return declaration;
    }

    /// <summary>The latest declaration `boson deploy` started for this repository, or null when there is none.</summary>
    public DeclareStatusResponse? GetDeclareStatus(string repo)
    {
        var run = TryGetDeclaration(repo);

        if (run is null) return null;

        if (!run.IsCompleted) return new DeclareStatusResponse("declaring", null, []);

        var declaration = run.Result;

        return new DeclareStatusResponse(
            declaration.Error is null ? "done" : "failed", declaration.Error, [.. declaration.Warnings]);
    }

    /// <summary>
    /// Whether a declaration `boson deploy` started for this repository is
    /// still running. Its deployments appear one at a time, so the first of
    /// them existing does not mean the project is declared.
    /// </summary>
    public bool IsDeclaring(string repo) => TryGetDeclaration(repo) is { IsCompleted: false };

    internal Task<Declaration>? TryGetDeclaration(string repo)
    {
        lock (_gate) return _declarations.GetValueOrDefault(repo);
    }

    /// <summary>
    /// What a saved project still needs before it serves anything: the clone,
    /// a deployment per branch `_boson.yml` names outright, their routes, and
    /// the App's webhook moved onto one of their hostnames. `boson add` runs it
    /// once the row exists, and `boson deploy` runs it again for a project the
    /// first attempt left with no deployments.
    /// </summary>
    public async Task<Declaration> DeclareAsync(Project project)
    {
        var repo = project.Repo;
        var warnings = new List<string>();
        var envSets = new List<string>();

        // Two declarations of one project would otherwise both insert the same
        // deployments, and the second would fail on the hostname. One gate for
        // every repository, since declarations are rare and port allocation
        // between them is then serialised too.
        await _declaring.WaitAsync();

        try
        {
            if (deployments.ListActiveForRepo(repo).Count > 0) return new Declaration(null, [], []);

            InstallationToken token;
            string defaultBranch;
            string checkoutDir;

            try
            {
                token = await minter.MintAsync(repo);

                // Whatever GitHub calls the default branch, rather than a guess
                // at `main` that a repository is free to disagree with.
                defaultBranch = await github.GetDefaultBranchAsync(repo, token.Value);

                checkoutDir = paths.CheckoutDir(repo, BranchSlug.From(defaultBranch));

                await git.FetchAndResetAsync(checkoutDir, repo, defaultBranch, token.Value);
            }
            catch (GithubApiException e) when (e.Status == 404)
            {
                // GitHub answers 404 for a repository a token may not see, so
                // on a repository the operator has just named this is nearly
                // always the installation's reach and not a missing repository.
                logger.LogWarning(e, "{Repo}: the installation cannot read the repository", repo);

                return new Declaration(
                    $"the GitHub App {project.GithubAppSlug} cannot read {repo}: it is not installed on " +
                    $"{RepoName.Split(repo).Org}, or its installation was not granted this repository. " +
                    $"Grant the repository in the App's installation settings and run boson deploy {repo}. " +
                    $"If the App was installed on another account, run boson remove {repo} --purge, " +
                    $"delete the App and add the project again (GitHub: {e.Message})",
                    warnings, envSets);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "{Repo}: initial fetch failed; `boson deploy` retries it", repo);
                return new Declaration($"initial fetch failed: {e.Message}", warnings, envSets);
            }

            var declared = BosonFile.Find(checkoutDir, out var problem);

            if (declared is null)
            {
                // A file that does not read is fixed with a push, and the next
                // `boson deploy` reads it again.
                var error = problem ?? $"no {BosonFile.FileName} at the root of {repo}";
                logger.LogWarning("{Repo}: {Error}", repo, error);
                return new Declaration(error, warnings, envSets);
            }

            // Fixed with a push like any other problem in the file, so it is
            // reported the same way and the project is kept.
            var reserved = declared.AdminHostnameProblem(platform.Get(PlatformRepository.AdminHostname))
                           ?? declared.Deployments
                               .Where(d => !d.IsPattern)
                               .Select(d => Deployer.ClaimedElsewhere(
                                   deployments.ListActive(), null, d.Aliases.Append(d.Hostname)))
                               .FirstOrDefault(problem => problem is not null);

            if (reserved is not null)
            {
                logger.LogWarning("{Repo}: {Error}", repo, reserved);
                return new Declaration(reserved, warnings, envSets);
            }

            // The clone can take minutes, and `boson remove` does not wait for
            // it. Deployments made for a project archived meanwhile would hold
            // their hostname and port for good: every query that lists them
            // looks through the project, and would never show them again.
            if (Removed(project))
                return new Declaration($"{repo} was removed while it was being declared", warnings, envSets);

            var created = new List<long>();

            try
            {
                await CreateDeploymentsAsync(project, declared, defaultBranch, warnings, envSets, created);

                await caddy.SyncAsync();
            }
            catch
            {
                // `boson deploy` declares a project that has no deployments,
                // so the ones made before the failure would pass for the whole
                // set and the webhook address would never move. Withdrawn, the
                // next attempt makes all of them. By id, because a listing
                // would miss them if the project has gone.
                foreach (var id in created) deployments.Archive(id);

                throw;
            }


            // The App was created pointing at the control plane, which GitHub
            // may not be able to reach. The webhook is repository-level, so any
            // deployment hostname can carry it.
            var primary = deployments.GetByBranch(repo, defaultBranch)
                          ?? deployments.ListActiveForRepo(repo).FirstOrDefault();

            if (primary is null)
            {
                // Every entry is a pattern, so no deployment exists to carry the
                // address, and the ones that will exist are created by pushes
                // that GitHub cannot deliver until it moves. Nothing later fixes
                // this on its own.
                warnings.Add(
                    $"{BosonFile.FileName} names no branch outright, so the App's webhook address " +
                    $"is still {platform.Get(PlatformRepository.AdminHostname)}, which GitHub may not " +
                    "reach. Set the address by hand, or remove and add the project again after " +
                    "declaring one branch outright");

                logger.LogWarning(
                    "{Repo}: no deployment to carry the webhook address; it stays on the control plane",
                    repo);
            }
            else
            {
                try
                {
                    await github.SetWebhookUrlAsync(
                        minter.JwtFor(project.GithubAppId, project.GithubAppPem),
                        $"https://{primary.Hostname}{CaddyConfigBuilder.WebhookPathPrefix}{repo}");
                }
                catch (Exception e)
                {
                    warnings.Add(
                        $"the App's webhook address is still {platform.Get(PlatformRepository.AdminHostname)}; " +
                        $"set it to https://{primary.Hostname}{CaddyConfigBuilder.WebhookPathPrefix}{repo} " +
                        $"by hand, or pushes will not deploy ({e.Message})");
                    logger.LogWarning(e, "{Repo}: could not move the App's webhook address", repo);
                }
            }

            return new Declaration(null, warnings, envSets);
        }
        finally
        {
            _declaring.Release();
        }
    }

    /// <summary>
    /// One deployment per branch the file names outright. A pattern entry
    /// describes branches that may not exist yet, so its deployments are
    /// created by the pushes that produce them.
    /// </summary>
    private bool Removed(Project project) => projects.GetByRepo(project.Repo)?.Id != project.Id;

    private async Task CreateDeploymentsAsync(
        Project project, BosonFile declared, string defaultBranch,
        List<string> warnings, List<string> envSets, List<long> created)
    {
        foreach (var declaration in declared.Deployments.Where(d => !d.IsPattern))
        {
            var hostname = declaration.HostnameFor(declaration.Branch);

            var addresses = await dns.ResolveAsync(hostname);

            if (addresses.Count == 0)
                warnings.Add(
                    $"{hostname} does not resolve; create the DNS record or Caddy will keep " +
                    "retrying certificate issuance");
            else if (!dns.AnyMatchesLocalInterface(addresses))
                warnings.Add(
                    $"{hostname} resolves to no local interface (common and legitimate behind NAT); " +
                    "whether it points at this host is proven by opening the site");

            created.Add(deployments.Insert(new Deployment
            {
                ProjectId = project.Id,
                Repo = project.Repo,
                Branch = declaration.Branch,
                BranchSlug = BranchSlug.From(declaration.Branch),
                Hostname = hostname,
                Aliases = string.Join(',', declaration.Aliases),
                Port = PortAllocator.Allocate(
                    deployments.ListActive().Select(d => d.Port), PortAllocator.IsFreeOnHost),
                EnvSet = declaration.Env,
                ExpireAfter = declaration.ExpireAfter,
            }));

            logger.LogInformation(
                "{Repo}#{Branch}: deployment declared for {Hostname}", project.Repo, declaration.Branch, hostname);
        }

        envSets.AddRange(declared.Deployments
            .Select(d => d.Env)
            .Where(set => set is not null)
            .Distinct()
            .Order()!);

        if (declared.Match(defaultBranch) is null)
            warnings.Add(
                $"{BosonFile.FileName} declares no deployment for {defaultBranch}, the default branch");
    }

    public AddStatusResponse? GetStatus(string token)
    {
        var entry = Lookup(token);
        if (entry is null) return null;
        var phase = entry.Phase switch
        {
            SetupPhase.AwaitingManifest => "awaiting_manifest",
            SetupPhase.AwaitingInstall => "awaiting_install",
            SetupPhase.Finalizing => "finalizing",
            SetupPhase.Fetching => "fetching",
            SetupPhase.Done => "done",
            SetupPhase.FetchFailed => "fetch_failed",
            SetupPhase.DeclareFailed => "declare_failed",
            _ => "failed",
        };
        return new AddStatusResponse(phase, null, entry.Error, [.. entry.Warnings], [.. entry.EnvSets]);
    }

    internal Task? TryGetFinalisation(string token) => Lookup(token)?.Finalisation;

    private PendingSetup? Lookup(string state)
    {
        if (string.IsNullOrEmpty(state)) return null;
        lock (_gate)
        {
            Sweep();
            return _pending.GetValueOrDefault(state);
        }
    }

    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var (token, entry) in _pending.Where(kv => now - kv.Value.CreatedAt > Ttl).ToList())
        {
            _pending.Remove(token);
            logger.LogInformation("setup entry for {Repo} expired", entry.Repo);
        }
    }
}
