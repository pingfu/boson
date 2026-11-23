using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Boson.Caddy;
using Boson.Storage;
using Boson.Util;
using Spectre.Console;

namespace Boson.Commands;

public static class StatusCommand
{
    public static Command Create()
    {
        var jsonOpt = new Option<bool>("--json") { Description = "Machine-readable output" };
        var cmd = new Command("status", "Platform and projects: version, admin hostname, containers, last deploy");

        cmd.Options.Add(jsonOpt);
        cmd.SetAction((parseResult, ct) => RunAsync(parseResult.GetValue(jsonOpt), ct));

        return cmd;
    }

    public static async Task<int> RunAsync(bool json, CancellationToken ct)
    {
        var paths = new BosonPaths();
        var db = new Db(paths.DbPath);

        if (!db.Exists())
        {
            Console.Error.WriteLine("no boson database found — run boson init first");
            return ExitCodes.UserError;
        }

        new Migrator(db).CheckCompatibility();

        var platform = new PlatformRepository(db);
        var deployments = new DeploymentsRepository(db);
        var deploys = new DeploysRepository(db);
        var docker = new DockerCli(new ProcessRunner());

        var adminHostname = platform.Get(PlatformRepository.AdminHostname);

        // Started now and collected after the listing, so asking GitHub costs
        // no time of its own unless it is the slowest thing here.
        var latestRelease = ProbeLatestReleaseAsync(ct);

        // The daemon's own version, not this binary's: they differ when a new
        // binary is installed and the restart hasn't happened yet, and the
        // daemon is the one serving webhooks.
        var daemon = await ProbeDaemonAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var rows = new List<DeploymentEntry>();

        foreach (var d in deployments.ListActive())
        {
            var ps = await docker.ComposePsAsync(RepoName.ComposeProjectName(d.Repo, d.BranchSlug), ct);
            var containers = ps.Ok ? SummariseComposePs(ps.StdOut) : "unavailable";
            var last = deploys.GetLatestForDeployment(d.Id);

            rows.Add(new DeploymentEntry(
                d.Repo, d.Branch, d.Hostname, [.. d.AliasList], d.Port, d.WebhookActive,
                containers,
                last is null
                    ? null
                    : new LastDeployEntry(last.Id, last.Status.AsDbValue(), last.CommitSha, last.StartedAt,
                        last.FinishedAt, last.LogPath, last.Error),

                // A pending flag with nothing running means a redeploy was dropped.
                DeployPendingIdle: d.DeployPending && last?.Status != DeployStatus.Running));
        }

        await DbOwnership.ChownToBosonAsync(new ProcessRunner(), paths.DbPath);

        var latest = await latestRelease;

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new StatusEntry(adminHostname, daemon is not null, daemon?.Version,
                    daemon?.StartedAt, VersionInfo.Version, latest, rows),
                StatusJson.Default.StatusEntry));

            return ExitCodes.Success;
        }

        // Labels bold white and values light yellow, so the two header lines
        // read at the same weight as the table under them. The update notice
        // takes a colour of its own because it is the one thing to act on.
        var update = UpdateNotice(daemon, latest) is { } notice ? $" [green]{Markup.Escape(notice)}[/]" : "";

        AnsiConsole.MarkupLine(
            $"[bold white]Boson version[/]   [khaki1]{Markup.Escape(DescribeDaemon(daemon, now))}[/]{update}");
        AnsiConsole.MarkupLine(
            $"[bold white]Admin url[/]       [khaki1]{Markup.Escape(AdminUrl(adminHostname))}[/]");
        AnsiConsole.WriteLine();

        if (rows.Count == 0)
        {
            Console.WriteLine("no deployments — add a project with: boson add <org/name>");
            return ExitCodes.Success;
        }

        var table = new Table().Border(TableBorder.Rounded);

        table.AddColumn("repo");
        table.AddColumn("hostname");
        table.AddColumn("published port");
        table.AddColumn("branch");
        table.AddColumn("webhook");
        table.AddColumn("containers");
        table.AddColumn("last deploy");

        foreach (var r in rows)
        {
            var lastText = r.LastDeploy is null
                ? "never"
                : $"#{r.LastDeploy.Id} {r.LastDeploy.Status} " +
                  $"{Shorten(r.LastDeploy.CommitSha)} " +
                  Timestamp(r.LastDeploy.FinishedAt ?? r.LastDeploy.StartedAt, now);

            table.AddRow(
                Markup.Escape(r.Repo),
                Markup.Escape(FormatHostname(r.Hostname, r.Aliases)),
                r.Port.ToString(),
                Markup.Escape(r.Branch),
                r.WebhookActive ? "active" : "inactive",
                Markup.Escape(r.Containers),
                Markup.Escape(lastText));
        }

        AnsiConsole.Write(table);

        // The branch is named in the command because a project serving more
        // than one refuses a bare `boson deploy <repo>`, and this warning is
        // about one of them.
        foreach (var r in rows.Where(r => r.DeployPendingIdle))
            AnsiConsole.MarkupLine(
                $"[yellow]⚠ {Markup.Escape(r.Repo)} {Markup.Escape(r.Branch)}: a push arrived that was never deployed. " +
                $"Recover with: boson deploy {Markup.Escape(r.Repo)} --branch {Markup.Escape(r.Branch)}[/]");

        return ExitCodes.Success;
    }

    /// <summary>
    /// Reads what the daemon reports about itself, or null when nothing
    /// answers. Everything else here comes from the database and Docker, which
    /// answer whether or not the daemon is up, so this is the only call that
    /// distinguishes a running platform from a dead one.
    /// </summary>
    private static async Task<DaemonInfo?> ProbeDaemonAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        try
        {
            var body = await http.GetStringAsync(
                $"http://127.0.0.1:{CaddyConfigBuilder.DaemonPort}/_boson/health", ct);

            using var doc = JsonDocument.Parse(body);

            var version = doc.RootElement.TryGetProperty("version", out var v)
                ? v.GetString()
                : null;

            // Absent on a daemon older than this field, which is exactly the
            // half-upgraded case the version mismatch below reports.
            var startedAt = doc.RootElement.TryGetProperty("startedAt", out var s)
                            && DateTimeOffset.TryParse(s.GetString(), CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : (DateTimeOffset?)null;

            return new DaemonInfo(version, startedAt);
        }
        catch
        {
            return null;
        }
    }

    public const string LatestReleaseUrl = "https://api.github.com/repos/pingfu/boson/releases/latest";

    /// <summary>
    /// The latest release's version, or null when GitHub does not say within
    /// two seconds. A status command that hangs or fails because GitHub is
    /// slow, rate limiting or unreachable would be worse than one that says
    /// nothing about updates.
    /// </summary>
    private static async Task<string?> ProbeLatestReleaseAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        // GitHub refuses a request with no User-Agent.
        http.DefaultRequestHeaders.UserAgent.Add(
            new System.Net.Http.Headers.ProductInfoHeaderValue("boson", ShortenVersion(VersionInfo.Version).Split('+')[0]));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        try
        {
            return LatestFromJson(await http.GetStringAsync(LatestReleaseUrl, ct));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The release's tag without its leading `v`, or null when the body is not a release.</summary>
    internal static string? LatestFromJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("tag_name", out var tag)
                   && tag.ValueKind == JsonValueKind.String
                   && tag.GetString()!.TrimStart('v', 'V') is { Length: > 0 } version
                ? version
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="latest"/> is a later release than the one
    /// running. Versions that do not read as numbers compare as not newer, so
    /// an odd tag says nothing rather than something wrong.
    /// </summary>
    internal static bool IsNewer(string? latest, string? running)
    {
        static Version? Parse(string? text) =>
            Version.TryParse(text?.Split('+', '-')[0].TrimStart('v', 'V'), out var parsed) ? parsed : null;

        return Parse(latest) is { } l && Parse(running) is { } r && l > r;
    }

    /// <summary>What to say about a newer release, or null when there is nothing to say.</summary>
    internal static string? UpdateNotice(DaemonInfo? daemon, string? latest) =>
        daemon?.Version is { } running && IsNewer(latest, running) ? $"({latest} available)" : null;

    internal static string DescribeDaemon(DaemonInfo? daemon, DateTimeOffset now)
    {
        if (daemon?.Version is null)
            return "not responding — diagnose with: systemctl status boson";

        var text = $"running {ShortenVersion(daemon.Version)}";

        if (daemon.StartedAt is { } started)
            text += $", up {Uptime(now - started)}, since {started:yyyy-MM-dd HH:mm} UTC";

        // A mismatch means the binary was replaced without the restart that
        // puts it into service, which is a half-finished upgrade.
        if (daemon.Version != VersionInfo.Version)
            text += $" — this binary is {ShortenVersion(VersionInfo.Version)}, " +
                    "finish the upgrade with: systemctl restart boson";

        return text;
    }

    /// <summary>
    /// The control plane's address as something to open. Caddy serves it on 443
    /// like every other hostname, so the port stays implicit.
    /// </summary>
    internal static string AdminUrl(string? adminHostname) =>
        string.IsNullOrWhiteSpace(adminHostname) ? "unset" : $"https://{adminHostname}";

    /// <summary>Renders a stored timestamp with its age, so "is this stale" reads off the table.</summary>
    internal static string Timestamp(string? dbTime, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(dbTime)) return "";

        return ParseDbTime(dbTime) is { } when
            ? $"{dbTime} ({Ago(when, now)})"
            : dbTime;
    }

    internal static string FormatHostname(string hostname, IReadOnlyList<string> aliases) =>
        aliases.Count == 0 ? hostname : $"{hostname} (+{aliases.Count})";

    internal static DateTimeOffset? ParseDbTime(string? dbTime) => DbTime.Parse(dbTime);

    internal static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var elapsed = now - when;

        // A clock skew or a row written by a faster machine reads as the present
        // rather than as a negative age.
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        if (elapsed < TimeSpan.FromHours(1)) return Plural((int)elapsed.TotalMinutes, "minute") + " ago";
        if (elapsed < TimeSpan.FromDays(1)) return Plural((int)elapsed.TotalHours, "hour") + " ago";
        if (elapsed < TimeSpan.FromDays(60)) return Plural((int)elapsed.TotalDays, "day") + " ago";

        return Plural((int)(elapsed.TotalDays / 30), "month") + " ago";
    }

    internal static string Uptime(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        if (elapsed.TotalDays >= 1) return $"{(int)elapsed.TotalDays}d {elapsed.Hours}h";
        if (elapsed.TotalHours >= 1) return $"{elapsed.Hours}h {elapsed.Minutes}m";
        if (elapsed.TotalMinutes >= 1) return $"{elapsed.Minutes}m";

        return $"{elapsed.Seconds}s";
    }

    private static string Plural(int count, string unit) =>
        count == 1 ? $"1 {unit}" : $"{count} {unit}s";

    /// <summary>Trims the commit sha the build stamps onto the version down to a readable prefix.</summary>
    internal static string ShortenVersion(string version)
    {
        var plus = version.IndexOf('+');

        if (plus < 0) return version;

        var sha = version[(plus + 1)..];

        return sha.Length > 7 ? $"{version[..plus]}+{sha[..7]}" : version;
    }

    /// <summary>
    /// Compose's `ps --format json` emits a JSON array on some versions and one
    /// JSON object per line on others, and boson does not pin the host's compose
    /// version. Both shapes are read; anything else degrades to "unknown"
    /// rather than failing the whole listing.
    /// </summary>
    internal static string SummariseComposePs(string stdout)
    {
        var states = new List<string>();

        try
        {
            var text = stdout.Trim();

            if (text.Length == 0) return "none";

            if (text.StartsWith('['))
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var el in doc.RootElement.EnumerateArray())
                    AddState(states, el);
            }
            else
            {
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    using var doc = JsonDocument.Parse(line.Trim());
                    AddState(states, doc.RootElement);
                }
            }
        }
        catch (JsonException)
        {
            return "unknown";
        }

        if (states.Count == 0) return "none";

        return string.Join(", ", states
            .GroupBy(s => s)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key}({g.Count()})"));
    }

    private static void AddState(List<string> states, JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("State", out var state))
            states.Add(state.GetString() ?? "unknown");
    }

    private static string Shorten(string? sha) =>
        string.IsNullOrEmpty(sha) ? "" : sha.Length > 7 ? sha[..7] : sha;

    internal sealed record DaemonInfo(string? Version, DateTimeOffset? StartedAt);


    internal sealed record StatusEntry(
        string? AdminHostname, bool DaemonRunning, string? DaemonVersion,
        DateTimeOffset? DaemonStartedAt, string CliVersion, string? LatestVersion,
        IReadOnlyList<DeploymentEntry> Deployments);

    internal sealed record DeploymentEntry(
        string Repo, string Branch, string Hostname, IReadOnlyList<string> Aliases,
        int Port, bool WebhookActive,
        string Containers, LastDeployEntry? LastDeploy, bool DeployPendingIdle);

    internal sealed record LastDeployEntry(
        long Id, string Status, string? CommitSha, string? StartedAt,
        string? FinishedAt, string LogPath, string? Error);
}

/// <summary>
/// Source-generated so --json survives trimming: reflection over these records
/// is invisible to the trimmer, and what it produces when the properties are
/// trimmed away is an empty object at runtime rather than a build error.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StatusCommand.StatusEntry))]
internal sealed partial class StatusJson : JsonSerializerContext;
