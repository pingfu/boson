using Boson.Caddy;
using Boson.Deploy;
using Boson.Github;
using Boson.Storage;
using Boson.Util;
using System.Net;
using System.Text.Json;

namespace Boson.Tests.Support;

public sealed class FakeGitCli : IGitCli
{
    public int Calls;
    public Func<int, Task>? OnFetch;
    public Exception? Throw;
    public Action<string>? OnFetchDir;

    public async Task<string> FetchAndResetAsync(
        string projectDir, string repo, string branch, string token,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var call = Interlocked.Increment(ref Calls);
        OnFetchDir?.Invoke(projectDir);
        if (OnFetch is not null) await OnFetch(call);
        if (Throw is not null) throw Throw;
        log?.Invoke($"fake fetch #{call}");
        return $"{call:x8}" + new string('0', 32);
    }
}

public sealed class FakeDockerCli : IDockerCli
{
    public int UpExitCode;
    public int UpCalls;
    public readonly List<string> DownedProjects = [];
    public string PsStdOut = "";

    private static ProcessResult Ok() => new(0, "", "");

    public Task<ProcessResult> InfoAsync(CancellationToken ct = default) => Task.FromResult(Ok());
    public Task<ProcessResult> ComposeVersionAsync(CancellationToken ct = default) =>
        Task.FromResult(new ProcessResult(0, "2.27.0", ""));

    public Task<ProcessResult> ComposeUpBuildAsync(
        string projectName, string workingDirectory,
        Action<string>? log = null, CancellationToken ct = default)
    {
        Interlocked.Increment(ref UpCalls);
        log?.Invoke($"fake compose up {projectName}");
        return Task.FromResult(new ProcessResult(UpExitCode, "", UpExitCode == 0 ? "" : "build failed"));
    }

    public Task<ProcessResult> ComposeDownAsync(string projectName, CancellationToken ct = default)
    {
        DownedProjects.Add(projectName);
        return Task.FromResult(Ok());
    }

    public Task<ProcessResult> ComposePsAsync(string projectName, CancellationToken ct = default) =>
        Task.FromResult(new ProcessResult(0, PsStdOut, ""));

    public Task<ProcessResult> ComposeUpStdinAsync(string yaml, CancellationToken ct = default) =>
        Task.FromResult(Ok());

    public Task<ProcessResult> ComposeDownStdinAsync(string yaml, CancellationToken ct = default) =>
        Task.FromResult(Ok());

    public Task<ProcessResult> VolumeRemoveAsync(string volume, CancellationToken ct = default) =>
        Task.FromResult(Ok());
}

public sealed class FakeMinter : IInstallationTokenMinter
{
    public bool Fail;
    public Task<InstallationToken> MintAsync(string repo, CancellationToken ct = default) =>
        Fail
            ? throw new GithubApiException("simulated mint failure")
            : Task.FromResult(new InstallationToken("ghs_test", DateTimeOffset.UtcNow.AddHours(1)));
}

public sealed class FakeGithubClient : IGithubClient
{
    public ManifestConversion Conversion = new(
        42, "boson-site", "-----BEGIN RSA PRIVATE KEY-----\nfake\n-----END RSA PRIVATE KEY-----", "whsec_test");
    public bool FailConversion;
    public string? LastCode;

    public Task<ManifestConversion> ConvertManifestAsync(string code, CancellationToken ct = default)
    {
        LastCode = code;
        return FailConversion
            ? throw new GithubApiException("simulated conversion failure")
            : Task.FromResult(Conversion);
    }

    public Task<InstallationToken> CreateInstallationTokenAsync(
        string jwt, long installationId, CancellationToken ct = default) =>
        Task.FromResult(new InstallationToken("ghs_test", DateTimeOffset.UtcNow.AddHours(1)));
}

public sealed class FakeCaddySynchroniser : ICaddySynchroniser
{
    public int SyncCalls;
    public Task SyncAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref SyncCalls);
        return Task.CompletedTask;
    }
}

public sealed class FakeDnsResolver : IDnsResolver
{
    public bool Resolves = true;
    public bool MatchesLocal = true;
    /// <summary>Per-hostname override; the flat <see cref="Resolves"/> applies when unset.</summary>
    public Func<string, bool>? ResolvesHost;

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostname, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IPAddress>>(
            (ResolvesHost?.Invoke(hostname) ?? Resolves) ? [IPAddress.Parse("203.0.113.10")] : []);

    public bool AnyMatchesLocalInterface(IReadOnlyList<IPAddress> addresses) => MatchesLocal;
}

public sealed class FakeDeployer : IDeployer
{
    public readonly List<(string Repo, DeployTrigger Trigger)> Calls = [];
    public readonly TaskCompletionSource Invoked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<DeployResult> DeployAsync(
        string repo, DeployTrigger trigger,
        Action<long>? onStarted = null, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add((repo, trigger));
        onStarted?.Invoke(1);
        Invoked.TrySetResult();
        return Task.FromResult<DeployResult>(new DeployResult.Completed(1, true, 1));
    }

    public Task WaitForIdleAsync(TimeSpan timeout) => Task.CompletedTask;
}

/// <summary>Stub process runner keyed on "file firstArg" (e.g. "docker info").</summary>
public sealed class MapProcessRunner : IProcessRunner
{
    public readonly Dictionary<string, ProcessResult> Map = new();

    public Task<ProcessResult> RunAsync(
        string fileName, IReadOnlyList<string> args,
        string? workingDirectory = null, string? stdin = null,
        Action<string>? onOutputLine = null, CancellationToken ct = default)
    {
        var key = $"{fileName} {args.FirstOrDefault()}".TrimEnd();
        return Task.FromResult(Map.TryGetValue(key, out var result)
            ? result
            : new ProcessResult(-1, "", $"no stub for: {key}"));
    }
}

public sealed class MutableClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
