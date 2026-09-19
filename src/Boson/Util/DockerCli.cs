namespace Boson.Util;

public interface IDockerCli
{
    Task<ProcessResult> InfoAsync(CancellationToken ct = default);
    Task<ProcessResult> ComposeVersionAsync(CancellationToken ct = default);

    Task<ProcessResult> ComposeUpBuildAsync(
        string projectName, string workingDirectory, int hostPort,
        Action<string>? log = null, CancellationToken ct = default);

    /// <summary>The compose file as compose resolves it, variables substituted.</summary>
    Task<ProcessResult> ComposeConfigAsync(
        string projectName, string workingDirectory, int hostPort, CancellationToken ct = default);

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
        string projectName, string workingDirectory, int hostPort,
        Action<string>? log = null, CancellationToken ct = default) =>
        runner.RunAsync(
            "docker",
            ["compose", "--project-name", projectName, "up", "-d", "--build", "--quiet-pull"],
            workingDirectory,
            onOutputLine: log,
            environment: HostPortEnvironment(hostPort),
            ct: ct);

    public Task<ProcessResult> ComposeConfigAsync(
        string projectName, string workingDirectory, int hostPort, CancellationToken ct = default) =>
        runner.RunAsync(
            "docker",
            ["compose", "--project-name", projectName, "config", "--format", "json"],
            workingDirectory,
            environment: HostPortEnvironment(hostPort),
            ct: ct);

    /// <summary>
    /// The port boson allocated, for the compose file to publish on. Supplied
    /// per invocation rather than written into the checkout, so the number
    /// stays in the database and a `git reset --hard` cannot disagree with it.
    /// </summary>
    private static Dictionary<string, string> HostPortEnvironment(int hostPort) =>
        new() { [Deploy.ComposePorts.HostPortVariable] = hostPort.ToString() };

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
