using Boson.Util;

namespace Boson.Platform;

/// <summary>
/// Writes, verifies and removes /etc/systemd/system/boson.service (spec §8),
/// and wraps the systemctl calls init/uninstall need.
/// </summary>
public sealed class SystemdUnit(IProcessRunner runner, string unitPath)
{
    public static string Content => EmbeddedResources.SystemdUnit;

    public string UnitPath { get; } = unitPath;

    /// <summary>Returns true when the file was (re)written — drift is repaired on re-run.</summary>
    public bool WriteIfChanged()
    {
        if (File.Exists(UnitPath) && File.ReadAllText(UnitPath) == Content)
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
        File.WriteAllText(UnitPath, Content);
        return true;
    }

    public void Remove()
    {
        if (File.Exists(UnitPath)) File.Delete(UnitPath);
    }

    public Task<ProcessResult> DaemonReloadAsync(CancellationToken ct = default) =>
        runner.RunAsync("systemctl", ["daemon-reload"], ct: ct);

    public Task<ProcessResult> EnableAsync(CancellationToken ct = default) =>
        runner.RunAsync("systemctl", ["enable", "boson"], ct: ct);

    public Task<ProcessResult> RestartAsync(CancellationToken ct = default) =>
        runner.RunAsync("systemctl", ["restart", "boson"], ct: ct);

    public Task<ProcessResult> DisableNowAsync(CancellationToken ct = default) =>
        runner.RunAsync("systemctl", ["disable", "--now", "boson"], ct: ct);
}
