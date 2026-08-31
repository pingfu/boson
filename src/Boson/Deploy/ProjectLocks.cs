using System.Collections.Concurrent;

namespace Boson.Deploy;

/// <summary>
/// Per-project non-blocking locks. Plain process-local state is sufficient
/// because the daemon is the sole deploy executor (spec §8, §16).
/// </summary>
public sealed class ProjectLocks
{
    private readonly ConcurrentDictionary<string, byte> _held = new();

    public bool TryEnter(string repo) => _held.TryAdd(repo, 0);

    public void Exit(string repo) => _held.TryRemove(repo, out _);

    public bool IsHeld(string repo) => _held.ContainsKey(repo);
}
