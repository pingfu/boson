using Boson.Util;

namespace Boson.Storage;

/// <summary>
/// The one CLI hygiene rule from spec §8: the CLI chowns the DB and its
/// WAL/SHM sidecars to boson:boson after touching them, so the daemon never
/// loses write access.
/// </summary>
public static class DbOwnership
{
    public static async Task ChownToBosonAsync(IProcessRunner runner, string dbPath)
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess) return;

        var files = new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }
            .Where(File.Exists)
            .ToArray();

        if (files.Length == 0) return;
        
        await runner.RunAsync("chown", ["boson:boson", .. files]);
    }
}
