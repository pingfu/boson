using System.CommandLine;
using System.Text.Json;
using Boson.Storage;
using Boson.Util;
using Spectre.Console;

namespace Boson.Commands;

public static class ListCommand
{
    public static Command Create()
    {
        var jsonOpt = new Option<bool>("--json") { Description = "Machine-readable output" };
        var cmd = new Command("list", "All projects: status, containers, last deploy");
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

        var projects = new ProjectsRepository(db);
        var deploys = new DeploysRepository(db);
        var docker = new DockerCli(new ProcessRunner());

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
                    : new LastDeployEntry(last.Id, last.Status, last.CommitSha, last.StartedAt,
                        last.FinishedAt, last.LogPath, last.Error),
                // A pending flag with nothing running means a redeploy was dropped (spec §4).
                DeployPendingIdle: p.DeployPending && last?.Status != "running"));
        }
        await DbOwnership.ChownToBosonAsync(new ProcessRunner(), paths.DbPath);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
            return ExitCodes.Success;
        }

        if (rows.Count == 0)
        {
            Console.WriteLine("no projects — add one with: boson add <org/name> --hostname <host> --upstream-port <port>");
            return ExitCodes.Success;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("repo");
        table.AddColumn("hostname");
        table.AddColumn("port");
        table.AddColumn("branch");
        table.AddColumn("webhook");
        table.AddColumn("containers");
        table.AddColumn("last deploy");
        foreach (var r in rows)
        {
            var lastText = r.LastDeploy is null
                ? "never"
                : $"#{r.LastDeploy.Id} {r.LastDeploy.Status} " +
                  $"{Shorten(r.LastDeploy.CommitSha)} {r.LastDeploy.FinishedAt ?? r.LastDeploy.StartedAt}";
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

    internal sealed record ProjectListEntry(
        string Repo, string Hostname, int Port, string Branch, bool WebhookActive,
        string Containers, LastDeployEntry? LastDeploy, bool DeployPendingIdle);

    internal sealed record LastDeployEntry(
        long Id, string Status, string? CommitSha, string? StartedAt,
        string? FinishedAt, string LogPath, string? Error);
}
