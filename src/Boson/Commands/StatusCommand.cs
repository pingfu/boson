using System.CommandLine;
using System.Globalization;
using System.Text.Json;
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
        var projects = new ProjectsRepository(db);
        var deploys = new DeploysRepository(db);
        var docker = new DockerCli(new ProcessRunner());

        var adminHostname = platform.Get(PlatformRepository.AdminHostname);

        // The daemon's own version, not this binary's: they differ when a new
        // binary is installed and the restart hasn't happened yet, and the
        // daemon is the one serving webhooks.
        var daemon = await ProbeDaemonAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var rows = new List<ProjectListEntry>();

        foreach (var p in projects.ListActive())
        {
            var ps = await docker.ComposePsAsync(RepoName.ComposeProjectName(p.Repo), ct);
            var containers = ps.Ok ? SummariseComposePs(ps.StdOut) : "unavailable";
            var last = deploys.GetLatestForProject(p.Id);

            rows.Add(new ProjectListEntry(
                p.Repo, p.Hostname, p.UpstreamPort, p.Branch, p.WebhookActive,
                containers,
                last is null
                    ? null
                    : new LastDeployEntry(last.Id, last.Status.AsDbValue(), last.CommitSha, last.StartedAt,
                        last.FinishedAt, last.LogPath, last.Error),

                // A pending flag with nothing running means a redeploy was dropped.
                DeployPendingIdle: p.DeployPending && last?.Status != DeployStatus.Running));
        }

        await DbOwnership.ChownToBosonAsync(new ProcessRunner(), paths.DbPath);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new StatusEntry(adminHostname, daemon is not null, daemon?.Version,
                    daemon?.StartedAt, VersionInfo.Version, rows),
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                }));

            return ExitCodes.Success;
        }

        AnsiConsole.MarkupLine($"[grey]daemon[/]  {Markup.Escape(DescribeDaemon(daemon, now))}");
        AnsiConsole.MarkupLine($"[grey]admin [/]  {Markup.Escape(adminHostname ?? "unset")}");
        AnsiConsole.WriteLine();

        if (rows.Count == 0)
        {
            Console.WriteLine("no projects — add one with: boson add <org/name> --hostname <host> --host-port <port>");
            return ExitCodes.Success;
        }

        var table = new Table().Border(TableBorder.Rounded);

        table.AddColumn("repo");
        table.AddColumn("hostname");
        table.AddColumn("host port");
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
                Markup.Escape(r.Hostname),
                r.Port.ToString(),
                Markup.Escape(r.Branch),
                r.WebhookActive ? "active" : "inactive",
                Markup.Escape(r.Containers),
                Markup.Escape(lastText));
        }

        AnsiConsole.Write(table);

        foreach (var r in rows.Where(r => r.DeployPendingIdle))
            AnsiConsole.MarkupLine(
                $"[yellow]⚠ {Markup.Escape(r.Repo)}: a push arrived that was never deployed — recover with: boson deploy {Markup.Escape(r.Repo)}[/]");

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

    /// <summary>Renders a stored timestamp with its age, so "is this stale" reads off the table.</summary>
    internal static string Timestamp(string? dbTime, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(dbTime)) return "";

        return ParseDbTime(dbTime) is { } when
            ? $"{dbTime} ({Ago(when, now)})"
            : dbTime;
    }

    /// <summary>SQLite writes `datetime('now')`, which is UTC with no marker on it.</summary>
    internal static DateTimeOffset? ParseDbTime(string? dbTime) =>
        DateTime.TryParse(dbTime, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc))
            : null;

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
        DateTimeOffset? DaemonStartedAt, string CliVersion,
        IReadOnlyList<ProjectListEntry> Projects);

    internal sealed record ProjectListEntry(
        string Repo, string Hostname, int Port, string Branch, bool WebhookActive,
        string Containers, LastDeployEntry? LastDeploy, bool DeployPendingIdle);

    internal sealed record LastDeployEntry(
        long Id, string Status, string? CommitSha, string? StartedAt,
        string? FinishedAt, string LogPath, string? Error);
}
