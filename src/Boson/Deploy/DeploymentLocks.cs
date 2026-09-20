using System.Collections.Concurrent;

namespace Boson.Deploy;

/// <summary>
/// Non-blocking locks, one per deployment rather than per repository: two
/// branches of one repository are separate containers on separate ports with
/// nothing to serialise between them. Plain process-local state is sufficient
/// because the daemon is the sole deploy executor.
/// </summary>
public sealed class DeploymentLocks
{
    private readonly ConcurrentDictionary<string, byte> _held = new();

    public bool TryEnter(string key) => _held.TryAdd(key, 0);

    public void Exit(string key) => _held.TryRemove(key, out _);

    public bool IsHeld(string key) => _held.ContainsKey(key);
}
