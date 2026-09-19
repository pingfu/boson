using Boson.Util;
using Xunit;

namespace Boson.Tests.Integration;

/// <summary>
/// Against a local bare repo: first call initialises an empty directory,
/// second call after a new push lands the new tip, force-push is tolerated,
/// untracked files survive.
/// </summary>
public sealed class GitCliIntegrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"boson-git-it-{Guid.NewGuid():N}");
    private readonly ProcessRunner _runner = new();
    private string BarePath => Path.Combine(_root, "origin.git");
    private string WorkPath => Path.Combine(_root, "work");
    private string CheckoutPath => Path.Combine(_root, "checkout");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task GitAsync(string cwd, params string[] args)
    {
        var result = await _runner.RunAsync("git", args, cwd);
        Assert.True(result.Ok, $"git {string.Join(' ', args)} failed: {result.StdErr}");
    }

    private async Task<string> HeadOfWorkAsync()
    {
        var result = await _runner.RunAsync("git", ["rev-parse", "HEAD"], WorkPath);
        return result.StdOut.Trim();
    }

    private async Task SetUpOriginAsync()
    {
        Directory.CreateDirectory(BarePath);
        Directory.CreateDirectory(WorkPath);
        await GitAsync(BarePath, "init", "--bare", "--initial-branch=main", ".");
        await GitAsync(WorkPath, "init", "--initial-branch=main", ".");
        await GitAsync(WorkPath, "config", "user.email", "test@example.com");
        await GitAsync(WorkPath, "config", "user.name", "boson test");
        File.WriteAllText(Path.Combine(WorkPath, "app.txt"), "v1");
        await GitAsync(WorkPath, "add", ".");
        await GitAsync(WorkPath, "commit", "-m", "v1");
        await GitAsync(WorkPath, "push", BarePath, "main");
    }

    [IntegrationFact]
    public async Task Init_fetch_reset_lands_tips_tolerates_force_push_and_keeps_untracked_files()
    {
        await SetUpOriginAsync();
        var git = new LocalGitCli(_runner, BarePath);

        // First deploy: empty directory holding an operator-dropped file.
        Directory.CreateDirectory(CheckoutPath);
        var envPath = Path.Combine(CheckoutPath, ".env");
        File.WriteAllText(envPath, "SECRET=1");

        var sha1 = await git.FetchAndResetAsync(CheckoutPath, "acme/site", "main", "tok");
        Assert.Equal(await HeadOfWorkAsync(), sha1);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(CheckoutPath, "app.txt")));

        // New push lands the new tip.
        File.WriteAllText(Path.Combine(WorkPath, "app.txt"), "v2");
        await GitAsync(WorkPath, "commit", "-am", "v2");
        await GitAsync(WorkPath, "push", BarePath, "main");

        var sha2 = await git.FetchAndResetAsync(CheckoutPath, "acme/site", "main", "tok");
        Assert.NotEqual(sha1, sha2);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(CheckoutPath, "app.txt")));

        // Force-push on the tracked branch is tolerated (the +refspec).
        File.WriteAllText(Path.Combine(WorkPath, "app.txt"), "v2-amended");
        await GitAsync(WorkPath, "commit", "-a", "--amend", "-m", "v2 amended");
        await GitAsync(WorkPath, "push", "--force", BarePath, "main");

        var sha3 = await git.FetchAndResetAsync(CheckoutPath, "acme/site", "main", "tok");
        Assert.NotEqual(sha2, sha3);
        Assert.Equal("v2-amended", File.ReadAllText(Path.Combine(CheckoutPath, "app.txt")));

        // Untracked files survive every deploy: reset --hard touches only
        // tracked files and boson never runs git clean.
        Assert.Equal("SECRET=1", File.ReadAllText(envPath));
    }
}
