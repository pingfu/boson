using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Boson.Caddy;
using Boson.Deploy;
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
    IPlatformRepository platform,
    ICaddySynchroniser caddy,
    IGitCli git,
    IInstallationTokenMinter minter,
    IGithubClient github,
    ProjectLocks locks,
    IDnsResolver dns,
    BosonPaths paths,
    TimeProvider clock,
    ILogger logger)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
    private static readonly int[] ReservedPorts = [80, 443, 2019, 9000];

    private sealed class PendingSetup
    {
        public required string Token { get; init; }
        public required string Repo { get; init; }
        public required string Hostname { get; init; }
        public required int Port { get; init; }
        public required string Branch { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
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
        if (request.Port is < 1 or > 65535)
            throw new BosonValidationException($"port out of range: {request.Port}");
        if (ReservedPorts.Contains(request.Port))
            throw new BosonValidationException(
                $"port {request.Port} is reserved (80/443 Caddy, 2019 its admin API, 9000 the daemon)");
        if (string.IsNullOrWhiteSpace(request.Hostname))
            throw new BosonValidationException("hostname is required");
        var hostname = request.Hostname.Trim().ToLowerInvariant();
        var branch = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch.Trim();

        foreach (var p in projects.ListActive())
        {
            if (p.Repo == repo)
                throw new BosonValidationException($"repo already added: {repo}");
            if (p.Hostname == hostname)
                throw new BosonValidationException($"hostname already claimed by {p.Repo}: {hostname}");
            if (p.UpstreamPort == request.Port)
                throw new BosonValidationException($"port already claimed by {p.Repo}: {request.Port}");
        }

        var warnings = new List<string>();
        var addresses = await dns.ResolveAsync(hostname, ct);
        if (addresses.Count == 0)
            throw new BosonValidationException(
                $"hostname does not resolve: {hostname} — create the DNS record first");
        if (!dns.AnyMatchesLocalInterface(addresses))
            warnings.Add(
                $"{hostname} resolves to no local interface (common and legitimate behind NAT); " +
                "whether it points at this host is proven by opening the site");

        // Every project gets a www redirect route, so Caddy will request a
        // certificate for that name too. Missing record: Caddy retries the ACME
        // challenge with backoff and the project's own hostname is unaffected.
        if (!hostname.StartsWith("www.", StringComparison.Ordinal)
            && (await dns.ResolveAsync($"www.{hostname}", ct)).Count == 0)
            warnings.Add(
                $"www.{hostname} does not resolve; boson serves a redirect for it anyway, so " +
                "Caddy will keep retrying certificate issuance until the record exists");

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var entry = new PendingSetup
        {
            Token = token,
            Repo = repo,
            Hostname = hostname,
            Port = request.Port,
            Branch = branch,
            CreatedAt = clock.GetUtcNow(),
        };
        lock (_gate)
        {
            Sweep();
            _pending[token] = entry;
        }

        return new AddStartResponse(
            $"https://{admin}/_boson/setup-app/start?state={token}", token, [.. warnings]);
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
            // Deliveries land on the project's own public hostname; only the
            // browser-driven redirects use the (possibly private) admin name.
            ["hook_attributes"] = new JsonObject
            {
                ["url"] =
                    $"https://{entry.Hostname}{CaddyConfigBuilder.WebhookPathPrefix}{entry.Repo}",
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
            projects.Insert(new Project
            {
                Repo = entry.Repo,
                Hostname = entry.Hostname,
                UpstreamPort = entry.Port,
                Branch = entry.Branch,
                GithubAppId = credentials.Id,
                GithubAppSlug = credentials.Slug,
                GithubInstallationId = installationId,
                GithubWebhookSecret = credentials.WebhookSecret,
                GithubAppPem = credentials.Pem,
            });

            await caddy.SyncAsync();
            entry.Phase = SetupPhase.Fetching;

            // Initial fetch under the project's lock; lock held means a
            // deploy is already running the identical fetch, so skip it.
            if (locks.TryEnter(entry.Repo))
            {
                try
                {
                    var token = await minter.MintAsync(entry.Repo);
                    await git.FetchAndResetAsync(
                        paths.ProjectDir(entry.Repo), entry.Repo, entry.Branch, token.Value);
                }
                catch (Exception e)
                {
                    entry.Phase = SetupPhase.FetchFailed;
                    entry.Error = $"initial fetch failed: {e.Message}";
                    logger.LogWarning(e, "{Repo}: initial fetch failed; `boson deploy` retries it", entry.Repo);
                    return;
                }
                finally
                {
                    locks.Exit(entry.Repo);
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
        return new AddStatusResponse(phase, null, entry.Error);
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
