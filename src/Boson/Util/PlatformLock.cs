namespace Boson.Util;

/// <summary>
/// init/uninstall mutual exclusion (spec §16): a filesystem lock, because
/// those commands contend CLI-vs-CLI across processes.
/// </summary>
public sealed class PlatformLock : IDisposable
{
    private readonly FileStream _stream;

    private PlatformLock(FileStream stream) => _stream = stream;

    public static PlatformLock? TryAcquire(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            
            return new PlatformLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
