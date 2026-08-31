namespace Boson.Tests.Support;

public sealed class TempDirs : IDisposable
{
    public string Root { get; }
    public BosonPaths Paths { get; }

    public TempDirs()
    {
        Root = Path.Combine(Path.GetTempPath(), $"boson-test-{Guid.NewGuid():N}");
        Paths = new BosonPaths(
            dataDir: Path.Combine(Root, "data"),
            logDir: Path.Combine(Root, "log"),
            srvDir: Path.Combine(Root, "srv"));
        Directory.CreateDirectory(Paths.DataDir);
        Directory.CreateDirectory(Paths.LogDir);
        Directory.CreateDirectory(Paths.DeployLogsDir);
        Directory.CreateDirectory(Paths.SrvDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
