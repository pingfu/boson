using Boson.Util;

namespace Boson.Storage;

/// <summary>
/// CLI hygiene: the CLI runs as root, the daemon as `boson`. SQLite creates the
/// -wal and -shm sidecars owned by whoever writes first, so a root-run command
/// that touches the DB can leave the daemon unable to write to its own database.
/// Every CLI path that opens the DB chowns it back.
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
