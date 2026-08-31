using Boson.Util;

namespace Boson.Platform;

public sealed record PreflightFailure(string Problem, string Detail, string Remedy);

/// <summary>
/// Spec §10 step 1: verify every software prerequisite, install nothing.
/// All checks run before any of them aborts; every failure is reported
/// together with its remediation.
/// </summary>
public sealed class Preflight(IProcessRunner runner, IHostProbe probe)
{
    public async Task<IReadOnlyList<PreflightFailure>> RunAsync(CancellationToken ct = default)
    {
        var failures = new List<PreflightFailure>();

        var dockerInfo = await runner.RunAsync("docker", ["info"], ct: ct);
        if (!dockerInfo.Ok)
            failures.Add(new PreflightFailure(
                "docker engine not running or not reachable",
                "Required to run Caddy and every project.",
                "Debian/Ubuntu:  apt-get install -y docker.io  (then: systemctl enable --now docker)"));

        var composeVersion = await runner.RunAsync("docker", ["compose", "version", "--short"], ct: ct);
        if (!composeVersion.Ok || !ComposeIsV2(composeVersion.StdOut))
            failures.Add(new PreflightFailure(
                "docker compose v2 plugin not found",
                "Required to run Caddy and every project.",
                "Debian/Ubuntu:  apt-get install -y docker-compose-plugin"));

        if (!probe.IsRoot)
            failures.Add(new PreflightFailure(
                "not running as root",
                "init creates the boson user and the systemd unit.",
                "Re-run as root:  sudo boson init <admin-hostname>"));

        var gitVersion = await runner.RunAsync("git", ["--version"], ct: ct);
        if (!gitVersion.Ok)
            failures.Add(new PreflightFailure(
                "git not found on PATH",
                "Required to fetch and update project repositories.",
                "Debian/Ubuntu:  apt-get install -y git\n      Alpine:         apk add git"));

        if (!probe.HasSystemd)
            failures.Add(new PreflightFailure(
                "systemd is not the init system",
                "The boson daemon is supervised by systemd; WSL default distros, containers and some minimal VMs aren't systemd hosts.",
                "Run boson on a systemd host (a standard Linux server image)."));

        return failures;
    }

    private static bool ComposeIsV2(string versionOutput)
    {
        var text = versionOutput.Trim().TrimStart('v', 'V');
        var core = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(core.Contains('.') ? core : core + ".0", out var v) && v.Major >= 2;
    }

    public static string Format(IReadOnlyList<PreflightFailure> failures)
    {
        var lines = new List<string>
        {
            $"✗ Preflight failed — {failures.Count} problem{(failures.Count == 1 ? "" : "s")}. " +
            "boson does not install host dependencies; please resolve these and re-run.",
            "",
        };
        foreach (var f in failures)
        {
            lines.Add($"  {f.Problem}");
            lines.Add($"      {f.Detail}");
            lines.Add($"      {f.Remedy}");
            lines.Add("");
        }
        lines.Add("No changes were made.");
        return string.Join(Environment.NewLine, lines);
    }
}
