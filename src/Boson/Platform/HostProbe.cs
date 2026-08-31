namespace Boson.Platform;

public interface IHostProbe
{
    bool IsRoot { get; }
    bool HasSystemd { get; }
}

public sealed class HostProbe : IHostProbe
{
    public bool IsRoot => Environment.IsPrivilegedProcess;

    public bool HasSystemd =>
        Directory.Exists("/run/systemd/system") && SystemctlOnPath();

    private static bool SystemctlOnPath() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, "systemctl")));
}
