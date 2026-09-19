namespace Boson.Util;

public interface IDockerCli
{
    Task<ProcessResult> InfoAsync(CancellationToken ct = default);
    Task<ProcessResult> ComposeVersionAsync(CancellationToken ct = default);

    Task<ProcessResult> ComposeUpBuildAsync(
        string projectName, string workingDirectory,
        Action<string>? log = null, CancellationToken ct = default);

    Task<ProcessResult> ComposeDownAsync(string projectName, CancellationToken ct = default);
    Task<ProcessResult> ComposePsAsync(string projectName, CancellationToken ct = default);

    /// <summary>Platform stack: YAML piped over stdin, never a file on disk.</summary>
    Task<ProcessResult> ComposeUpStdinAsync(string yaml, CancellationToken ct = default);
    Task<ProcessResult> ComposeDownStdinAsync(string yaml, CancellationToken ct = default);

    Task<ProcessResult> VolumeRemoveAsync(string volume, CancellationToken ct = default);
}

public sealed class DockerCli(IProcessRunner runner) : IDockerCli
{
    public Task<ProcessResult> InfoAsync(CancellationToken ct = default) =>
        runner.RunAsync("docker", ["info"], ct: ct);

    public Task<ProcessResult> ComposeVersionAsync(CancellationToken ct = default) =>
        runner.RunAsync("docker", ["compose", "version", "--short"], ct: ct);

    public Task<ProcessResult> ComposeUpBuildAsync(
        string projectName, string workingDirectory,
        Action<string>? log = null, CancellationToken ct = default) =>
        runner.RunAsync(
            "docker",
            ["compose", "--project-name", projectName, "up", "-d", "--build", "--quiet-pull"],
            workingDirectory,
            onOutputLine: log,
            ct: ct);

    public Task<ProcessResult> ComposeDownAsync(string projectName, CancellationToken ct = default) =>
        runner.RunAsync("docker", ["compose", "--project-name", projectName, "down"], ct: ct);

    public Task<ProcessResult> ComposePsAsync(string projectName, CancellationToken ct = default) =>
        runner.RunAsync(
            "docker",
            ["compose", "--project-name", projectName, "ps", "--format", "json"],
            ct: ct);

    public Task<ProcessResult> ComposeUpStdinAsync(string yaml, CancellationToken ct = default) =>
        runner.RunAsync("docker", ["compose", "-f", "-", "up", "-d"], stdin: yaml, ct: ct);

    public Task<ProcessResult> ComposeDownStdinAsync(string yaml, CancellationToken ct = default) =>
        runner.RunAsync("docker", ["compose", "-f", "-", "down"], stdin: yaml, ct: ct);

    public Task<ProcessResult> VolumeRemoveAsync(string volume, CancellationToken ct = default) =>
        runner.RunAsync("docker", ["volume", "rm", "-f", volume], ct: ct);
}
