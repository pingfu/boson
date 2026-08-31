namespace Boson;

/// <summary>
/// Host filesystem layout (spec §3). Environment overrides exist for foreground
/// development on non-Linux hosts; production always uses the fixed paths.
/// </summary>
public sealed class BosonPaths
{
    public string DataDir { get; }
    public string LogDir { get; }
    public string SrvDir { get; }

    public string DbPath => Path.Combine(DataDir, "boson.db");
    public string SocketPath => Path.Combine(DataDir, "boson.sock");
    public string LocksDir => Path.Combine(DataDir, "locks");
    public string PlatformLockPath => Path.Combine(LocksDir, "_platform.lock");
    public string TmpDir => Path.Combine(DataDir, "tmp");
    public string DeployLogsDir => Path.Combine(LogDir, "deploys");
    public string DaemonLogPath => Path.Combine(LogDir, "boson.log");
    public string UnitFilePath => "/etc/systemd/system/boson.service";

    public BosonPaths(string? dataDir = null, string? logDir = null, string? srvDir = null)
    {
        DataDir = dataDir
            ?? Environment.GetEnvironmentVariable("BOSON_DATA_DIR")
            ?? "/var/lib/boson";

        LogDir = logDir
            ?? Environment.GetEnvironmentVariable("BOSON_LOG_DIR")
            ?? "/var/log/boson";
            
        SrvDir = srvDir
            ?? Environment.GetEnvironmentVariable("BOSON_SRV_DIR")
            ?? "/srv";
    }

    public string ProjectDir(string repo)
    {
        var (org, name) = RepoName.Split(repo);

        return Path.Combine(SrvDir, org, name);
    }

    public string DeployLogPath(long deployId) => Path.Combine(DeployLogsDir, $"{deployId}.log");
}
