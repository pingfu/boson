namespace Boson.Util;

public interface IGitCli
{
    /// <summary>
    /// One git path for every deploy: init when .git is missing,
    /// then fetch the branch tip with the token inline and reset --hard.
    /// Returns the resulting HEAD sha.
    /// </summary>
    Task<string> FetchAndResetAsync(
        string projectDir, string repo, string branch, string token,
        Action<string>? log = null, CancellationToken ct = default);
}

public class GitCli(IProcessRunner runner) : IGitCli
{
    protected virtual string BuildFetchUrl(string repo, string token) => $"https://x-access-token:{token}@github.com/{repo}.git";

    public async Task<string> FetchAndResetAsync(
        string projectDir, string repo, string branch, string token,
        Action<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(projectDir);

        if (!Directory.Exists(Path.Combine(projectDir, ".git")))
        {
            log?.Invoke($"$ git init  (in {projectDir})");
            var init = await runner.RunAsync("git", ["init", "--quiet"], projectDir, ct: ct);
            if (!init.Ok) throw new GitException($"git init failed: {init.StdErr.Trim()}");
        }

        var fetchUrl = BuildFetchUrl(repo, token);
        var refspec = $"+refs/heads/{branch}:refs/remotes/origin/{branch}";

        log?.Invoke($"$ git fetch {Redact(fetchUrl, token)} --depth=1 --no-tags {refspec}");

        var fetch = await runner.RunAsync(
            "git", ["fetch", fetchUrl, "--depth=1", "--no-tags", refspec],
            projectDir,
            onOutputLine: line => log?.Invoke(Redact(line, token)),
            ct: ct);

        if (!fetch.Ok)
            throw new GitException($"git fetch failed: {Redact(fetch.StdErr.Trim(), token)}");

        log?.Invoke($"$ git reset --hard refs/remotes/origin/{branch}");

        var reset = await runner.RunAsync(
            "git", ["reset", "--hard", $"refs/remotes/origin/{branch}"],
            projectDir,
            onOutputLine: line => log?.Invoke(line),
            ct: ct);

        if (!reset.Ok)
            throw new GitException($"git reset failed: {reset.StdErr.Trim()}");

        var head = await runner.RunAsync("git", ["rev-parse", "HEAD"], projectDir, ct: ct);

        if (!head.Ok)
            throw new GitException($"git rev-parse failed: {head.StdErr.Trim()}");

        return head.StdOut.Trim();
    }

    private static string Redact(string text, string token) => token.Length == 0 ? text : text.Replace(token, "***");
}

public sealed class GitException(string message) : Exception(message);
