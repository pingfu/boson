using Boson.Deploy;
using Boson.Github;
using Boson.Serve;
using Boson.Storage;
using Boson.Tests.Support;
using Boson.Util;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Boson.Tests;

public sealed class ManifestFlowOrchestratorTests : IDisposable
{
    private readonly TempDb _db = new();
    private readonly TempDirs _dirs = new();
    private readonly ProjectsRepository _projects;
    private readonly PlatformRepository _platform;
    private readonly ProjectLocks _locks = new();
    private readonly FakeGitCli _git = new();
    private readonly FakeMinter _minter = new();
    private readonly FakeGithubClient _github = new();
    private readonly FakeCaddySynchroniser _caddy = new();
    private readonly FakeDnsResolver _dns = new();
    private readonly MutableClock _clock = new();
    private readonly ManifestFlowOrchestrator _orchestrator;

    public ManifestFlowOrchestratorTests()
    {
        _projects = new ProjectsRepository(_db.Db);
        _platform = new PlatformRepository(_db.Db);
        _platform.Set(PlatformRepository.AdminHostname, "deploy.example.com");
        _orchestrator = new ManifestFlowOrchestrator(
            _projects, _platform, _caddy, _git, _minter, _github, _locks, _dns,
            _dirs.Paths, _clock, NullLogger.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dirs.Dispose();
    }

    private static AddRequest Request(
        string repo = "acme/site", string hostname = "site.example.com",
        int port = 8080, string branch = "main") => new(repo, hostname, port, branch);

    private async Task<string> RunToInstalledAsync(AddRequest? request = null, long installationId = 777)
    {
        var start = await _orchestrator.BeginAddAsync(request ?? Request());
        Assert.NotNull(await Task.FromResult(_orchestrator.RenderStartPage(start.Token)));
        Assert.NotNull(await _orchestrator.HandleCallbackAsync(start.Token, "code-1"));
        Assert.True(_orchestrator.HandleInstalled(start.Token, installationId));
        await _orchestrator.TryGetFinalisation(start.Token)!;
        return start.Token;
    }

    [Fact]
    public async Task Begin_returns_public_setup_url_with_the_state_token()
    {
        var start = await _orchestrator.BeginAddAsync(Request());
        Assert.Equal(
            $"https://deploy.example.com/_boson/setup-app/start?state={start.Token}",
            start.SetupUrl);
        Assert.Empty(start.Warnings);
    }

    [Fact]
    public async Task Begin_warns_when_hostname_matches_no_local_interface()
    {
        _dns.MatchesLocal = false;
        var start = await _orchestrator.BeginAddAsync(Request());
        Assert.Single(start.Warnings);
    }

    [Fact]
    public async Task Begin_rejects_unresolvable_hostname()
    {
        _dns.Resolves = false;
        await Assert.ThrowsAsync<BosonValidationException>(() =>
            _orchestrator.BeginAddAsync(Request()));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(443)]
    [InlineData(2019)]
    [InlineData(9000)]
    public async Task Begin_rejects_reserved_ports(int port) =>
        await Assert.ThrowsAsync<BosonValidationException>(() =>
            _orchestrator.BeginAddAsync(Request(port: port)));

    [Fact]
    public async Task Begin_rejects_collisions_with_active_projects()
    {
        _projects.Insert(TestProjects.New());
        await Assert.ThrowsAsync<BosonValidationException>(() =>
            _orchestrator.BeginAddAsync(Request(repo: "acme/site", hostname: "x.example.com", port: 9001)));
        await Assert.ThrowsAsync<BosonValidationException>(() =>
            _orchestrator.BeginAddAsync(Request(repo: "acme/other", hostname: "site.example.com", port: 9001)));
        await Assert.ThrowsAsync<BosonValidationException>(() =>
            _orchestrator.BeginAddAsync(Request(repo: "acme/other", hostname: "x.example.com", port: 8080)));
    }

    [Fact]
    public async Task Start_page_carries_the_manifest()
    {
        var start = await _orchestrator.BeginAddAsync(Request());
        var html = _orchestrator.RenderStartPage(start.Token)!;

        Assert.Contains("boson-site", html);
        Assert.Contains("https://deploy.example.com/_boson/webhook/acme/site", html);
        Assert.Contains("https://deploy.example.com/_boson/setup-app/callback", html);
        Assert.Contains("https://deploy.example.com/_boson/setup-app/installed", html);
        Assert.Contains($"state={start.Token}", html);
        Assert.Contains("\"contents\":\"read\"", html);
    }

    [Fact]
    public void Unknown_state_token_renders_nothing()
    {
        Assert.Null(_orchestrator.RenderStartPage("nope"));
        Assert.Null(_orchestrator.GetStatus("nope"));
    }

    [Fact]
    public async Task Callback_exchanges_the_code_and_redirects_to_install()
    {
        var start = await _orchestrator.BeginAddAsync(Request());
        var redirect = await _orchestrator.HandleCallbackAsync(start.Token, "code-1");

        Assert.Equal("code-1", _github.LastCode);
        Assert.Equal(
            $"https://github.com/apps/boson-site/installations/new?state={start.Token}",
            redirect);
        Assert.Equal("awaiting_install", _orchestrator.GetStatus(start.Token)!.Phase);
    }

    [Fact]
    public async Task Installed_persists_the_complete_row_syncs_caddy_and_fetches()
    {
        var token = await RunToInstalledAsync();

        Assert.Equal("done", _orchestrator.GetStatus(token)!.Phase);
        var project = _projects.GetByRepo("acme/site");
        Assert.NotNull(project);
        Assert.Equal(42, project.GithubAppId);
        Assert.Equal("boson-site", project.GithubAppSlug);
        Assert.Equal(777, project.GithubInstallationId);
        Assert.Equal("whsec_test", project.GithubWebhookSecret);
        Assert.False(project.WebhookActive);
        Assert.Equal(1, _caddy.SyncCalls);
        Assert.Equal(1, _git.Calls);
        Assert.False(_locks.IsHeld("acme/site"));
    }

    [Fact]
    public async Task Fetch_failure_leaves_the_project_added()
    {
        _git.Throw = new GitException("network down");
        var token = await RunToInstalledAsync();

        var status = _orchestrator.GetStatus(token)!;
        Assert.Equal("fetch_failed", status.Phase);
        Assert.Contains("network down", status.Error);
        Assert.NotNull(_projects.GetByRepo("acme/site")); // boson deploy retries the fetch
    }

    [Fact]
    public async Task Held_lock_skips_the_initial_fetch()
    {
        _locks.TryEnter("acme/site");
        var token = await RunToInstalledAsync();

        Assert.Equal("done", _orchestrator.GetStatus(token)!.Phase);
        Assert.Equal(0, _git.Calls);
    }

    [Fact]
    public async Task Failed_conversion_marks_the_flow_failed_and_persists_nothing()
    {
        _github.FailConversion = true;
        var start = await _orchestrator.BeginAddAsync(Request());
        var redirect = await _orchestrator.HandleCallbackAsync(start.Token, "code-1");

        Assert.Null(redirect);
        Assert.Equal("failed", _orchestrator.GetStatus(start.Token)!.Phase);
        Assert.Null(_projects.GetByRepo("acme/site"));
    }

    [Fact]
    public async Task Abandoned_flow_persists_nothing()
    {
        var start = await _orchestrator.BeginAddAsync(Request());
        await _orchestrator.HandleCallbackAsync(start.Token, "code-1");
        // Browser never reaches /installed.
        Assert.Null(_projects.GetByRepo("acme/site"));
        Assert.Equal(0, _caddy.SyncCalls);
    }

    [Fact]
    public async Task Entries_expire_after_the_ttl()
    {
        var start = await _orchestrator.BeginAddAsync(Request());
        _clock.Now += ManifestFlowOrchestrator.Ttl + TimeSpan.FromSeconds(1);

        Assert.Null(_orchestrator.RenderStartPage(start.Token));
        Assert.Null(_orchestrator.GetStatus(start.Token));
        Assert.False(_orchestrator.HandleInstalled(start.Token, 777));
    }

    [Fact]
    public async Task Installed_without_credentials_is_refused()
    {
        var start = await _orchestrator.BeginAddAsync(Request());
        Assert.False(_orchestrator.HandleInstalled(start.Token, 777));
    }
}
