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
    Failed,
}

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

    public async Task<AddStartResponse> BeginAddAsync(AddRequest request, CancellationToken ct = default)
    {
        var admin = platform.Get(PlatformRepository.AdminHostname)
            ?? throw new BosonValidationException("platform not initialised; run boson init first");

        if (!RepoName.TryCanonicalise(request.Repo, out var repo))
            throw new BosonValidationException($"invalid repo (expected org/name): {request.Repo}");

        if (projects.GetByRepo(repo) is not null)
            throw new BosonValidationException($"repo already added: {repo}");

        // Hostnames come from `_boson.yml`, which lives in the repository, which
        // needs the App that this flow is about to create. So nothing about
        // what gets served is known yet, and the checks on it happen after the
        // clone rather than here.
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var entry = new PendingSetup
        {
            Token = token,
            Repo = repo,
            CreatedAt = clock.GetUtcNow(),
        };

        lock (_gate)
        {
            Sweep();
            _pending[token] = entry;
        }

        await Task.CompletedTask;

        return new AddStartResponse(
            $"https://{admin}/_boson/setup-app/start?state={token}", token, []);
    }

    public string? RenderStartPage(string state)
    {
        var entry = Lookup(state);
        if (entry is null) return null;
        var admin = platform.Get(PlatformRepository.AdminHostname)!;
        var (_, name) = RepoName.Split(entry.Repo);

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
        try
        {
            var credentials = entry.Credentials!;

            var projectId = projects.Insert(new Project
            {
                Repo = entry.Repo,
                GithubAppId = credentials.Id,
                GithubAppSlug = credentials.Slug,
                GithubInstallationId = installationId,
                GithubWebhookSecret = credentials.WebhookSecret,
                GithubAppPem = credentials.Pem,
            });

            entry.Phase = SetupPhase.Fetching;

            InstallationToken token;
            string defaultBranch;
            string checkoutDir;

            try
            {
                token = await minter.MintAsync(entry.Repo);

                // Whatever GitHub calls the default branch, rather than a guess
                // at `main` that a repository is free to disagree with.
                defaultBranch = await github.GetDefaultBranchAsync(entry.Repo, token.Value);

                checkoutDir = paths.CheckoutDir(entry.Repo, DnsLabel.From(defaultBranch));

                await git.FetchAndResetAsync(checkoutDir, entry.Repo, defaultBranch, token.Value);
            }
            catch (Exception e)
            {
                entry.Phase = SetupPhase.FetchFailed;
                entry.Error = $"initial fetch failed: {e.Message}";
                logger.LogWarning(e, "{Repo}: initial fetch failed; `boson deploy` retries it", entry.Repo);
                return;
            }

            var declared = BosonFile.Find(checkoutDir, out var problem);

            if (declared is null)
            {
                // The App and its keys are kept: GitHub issues them once, and a
                // file that does not read is fixed with a push.
                entry.Phase = SetupPhase.FetchFailed;
                entry.Error = problem ?? $"no {BosonFile.FileName} at the root of {entry.Repo}";
                logger.LogWarning("{Repo}: {Error}", entry.Repo, entry.Error);
                return;
            }

            await CreateDeploymentsAsync(entry, projectId, declared, defaultBranch);

            await caddy.SyncAsync();

            // The App was created pointing at the control plane, which GitHub
            // may not be able to reach. The webhook is repository-level, so any
            // deployment hostname can carry it.
            var primary = deployments.GetByBranch(entry.Repo, defaultBranch)
                          ?? deployments.ListActiveForRepo(entry.Repo).FirstOrDefault();

            if (primary is not null)
            {
                try
                {
                    await github.SetWebhookUrlAsync(
                        minter.JwtFor(credentials.Id, credentials.Pem),
                        $"https://{primary.Hostname}{CaddyConfigBuilder.WebhookPathPrefix}{entry.Repo}");
                }
                catch (Exception e)
                {
                    entry.Warnings.Add(
                        $"the App's webhook address is still {platform.Get(PlatformRepository.AdminHostname)}; " +
                        $"set it to https://{primary.Hostname}{CaddyConfigBuilder.WebhookPathPrefix}{entry.Repo} " +
                        $"by hand, or pushes will not deploy ({e.Message})");
                    logger.LogWarning(e, "{Repo}: could not move the App's webhook address", entry.Repo);
                }
            }

            entry.Phase = SetupPhase.Done;
            logger.LogInformation("{Repo}: project added", entry.Repo);
        }
        catch (Exception e)
        {
            entry.Phase = SetupPhase.Failed;
            entry.Error = e.Message;
            logger.LogError(e, "add flow failed for {Repo}", entry.Repo);
        }
    }

    /// <summary>
    /// One deployment per branch the file names outright. A pattern entry
    /// describes branches that may not exist yet, so its deployments are
    /// created by the pushes that produce them.
    /// </summary>
    private async Task CreateDeploymentsAsync(
        PendingSetup entry, long projectId, BosonFile declared, string defaultBranch)
    {
        foreach (var declaration in declared.Deployments.Where(d => !d.IsPattern))
        {
            var hostname = declaration.HostnameFor(declaration.Branch);

            var addresses = await dns.ResolveAsync(hostname);

            if (addresses.Count == 0)
                entry.Warnings.Add(
                    $"{hostname} does not resolve; create the DNS record or Caddy will keep " +
                    "retrying certificate issuance");
            else if (!dns.AnyMatchesLocalInterface(addresses))
                entry.Warnings.Add(
                    $"{hostname} resolves to no local interface (common and legitimate behind NAT); " +
                    "whether it points at this host is proven by opening the site");

            deployments.Insert(new Deployment
            {
                ProjectId = projectId,
                Repo = entry.Repo,
                Branch = declaration.Branch,
                DnsLabel = DnsLabel.From(declaration.Branch),
                Hostname = hostname,
                Aliases = string.Join(',', declaration.Aliases),
                HostPort = HostPortAllocator.Allocate(
                    deployments.ListActive().Select(d => d.HostPort), HostPortAllocator.IsFreeOnHost),
                EnvSet = declaration.Env,
                ExpireAfter = declaration.ExpireAfter,
            });

            logger.LogInformation(
                "{Repo}#{Branch}: deployment declared for {Hostname}", entry.Repo, declaration.Branch, hostname);
        }

        entry.EnvSets.AddRange(declared.Deployments
            .Select(d => d.Env)
            .Where(set => set is not null)
            .Distinct()
            .Order()!);

        if (declared.Match(defaultBranch) is null)
            entry.Warnings.Add(
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
