using Boson.Util;

namespace Boson.Platform;

/// <summary>
/// Writes, verifies and removes /etc/systemd/system/boson.service (spec §8),
/// and wraps the systemctl calls init/uninstall need.
/// </summary>
public sealed class SystemdUnit(IProcessRunner runner, string unitPath)
{
    /// <summary>The path the unit's ExecStart names; init puts the binary here.</summary>
    public const string ExecPath = "/usr/local/bin/boson";

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

    /// <summary>
    /// Copies the running binary to <see cref="ExecPath"/> when it isn't already
    /// there, so the unit never points at a path holding no executable (systemd
    /// 203/EXEC). Writes to a temp file and renames: replacing a binary that a
    /// running daemon is executing fails otherwise.
    /// Returns the path it installed from, or null when nothing was copied.
    /// </summary>
    public static string? InstallSelf()
    {
        var source = Environment.ProcessPath;
        if (source is null || !OperatingSystem.IsLinux()) return null;
        if (Path.GetFullPath(source) == ExecPath) return null;

        Directory.CreateDirectory(Path.GetDirectoryName(ExecPath)!);
        var staged = ExecPath + ".new";
        File.Copy(source, staged, overwrite: true);
        File.SetUnixFileMode(staged,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(staged, ExecPath, overwrite: true);
        return source;
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

    /// <summary>
    /// Clears a start-limit wedge. After repeated fast failures systemd refuses
    /// to start the unit at all ("start request repeated too quickly"), which
    /// would make init unable to recover the very state it exists to repair.
    /// </summary>
    public Task<ProcessResult> ResetFailedAsync(CancellationToken ct = default) =>
        runner.RunAsync("systemctl", ["reset-failed", "boson"], ct: ct);

    public Task<ProcessResult> DisableNowAsync(CancellationToken ct = default) =>
        runner.RunAsync("systemctl", ["disable", "--now", "boson"], ct: ct);
}
