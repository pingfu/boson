using Boson.Storage;

namespace Boson.Caddy;

public interface ICaddySynchroniser
{
    /// <summary>Rebuild the full config from DB state and POST /load it.</summary>
    Task SyncAsync(CancellationToken ct = default);
}

/// <summary>
/// The daemon's single writer for steady-state Caddy config pushes. Every push
/// is a full-config replace, so two concurrent ones would race to overwrite
/// each other wholesale; the mutex serialises them. `boson init` also pushes,
/// but only at bootstrap and recovery, never alongside normal operation.
/// </summary>
public sealed class CaddySynchroniser(
    CaddyConfigBuilder builder,
    ICaddyAdminClient client,
    IProjectsRepository projects,
    IPlatformRepository platform) : ICaddySynchroniser
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);

        try
        {
            var admin = platform.Get(PlatformRepository.AdminHostname)
                ?? throw new InvalidOperationException("control hostname not set; run boson init");
            using var cfg = builder.Build(projects.ListActive(), admin);

            await client.LoadConfigAsync(cfg, ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
