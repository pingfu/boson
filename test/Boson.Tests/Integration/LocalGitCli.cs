using Boson.Util;

namespace Boson.Tests.Integration;

/// <summary>
/// GitCli pointed at a local bare repository instead of github.com, so the
/// init + fetch + reset path (spec §13) is exercised without a network or a
/// real installation token.
/// </summary>
public sealed class LocalGitCli(IProcessRunner runner, string bareRepoPath) : GitCli(runner)
{
    protected override string BuildFetchUrl(string repo, string token) => bareRepoPath;
}
