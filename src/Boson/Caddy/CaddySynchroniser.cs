using Boson.Storage;

namespace Boson.Caddy;

public interface ICaddySynchroniser
{
    /// <summary>Rebuild the full config from DB state and POST /load it (spec §7).</summary>
    Task SyncAsync(CancellationToken ct = default);
}

/// <summary>
/// The daemon's single writer for steady-state Caddy config pushes; the mutex
/// is spec §16's "Caddy admin updates" row.
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
            var internalTls = platform.Get(PlatformRepository.AdminTlsInternal) == "1";

            using var cfg = builder.Build(projects.ListActive(), admin, internalTls);

            await client.LoadConfigAsync(cfg, ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
