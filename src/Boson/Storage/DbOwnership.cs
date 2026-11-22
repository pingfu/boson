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

        var files = Files(dbPath);

        if (files.Length == 0) return;

        RestrictMode(dbPath);

        await runner.RunAsync("chown", ["boson:boson", .. files]);
    }

    /// <summary>
    /// Owner read and write, nothing else. The database holds every project's
    /// App private key, and SQLite creates it with whatever the umask allows,
    /// which leaves the 0700 directory above it as the only protection. The
    /// sidecars are set too, though SQLite gives new ones the main file's mode.
    /// </summary>
    public static void RestrictMode(string dbPath)
    {
        if (OperatingSystem.IsWindows()) return;

        foreach (var file in Files(dbPath))
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string[] Files(string dbPath) =>
        new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }.Where(File.Exists).ToArray();
}
