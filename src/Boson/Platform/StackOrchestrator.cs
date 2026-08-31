using Boson.Util;

namespace Boson.Platform;

/// <summary>Platform compose stack — Caddy only (spec §9).</summary>
public sealed class StackOrchestrator(IDockerCli docker)
{
    public Task<ProcessResult> UpAsync(CancellationToken ct = default) =>
        docker.ComposeUpStdinAsync(ComposeTemplate.Render(), ct);

    public Task<ProcessResult> DownAsync(CancellationToken ct = default) =>
        docker.ComposeDownStdinAsync(ComposeTemplate.Render(), ct);
}
