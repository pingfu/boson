using Boson.Deploy;
using Boson.Github;
using Boson.Platform;
using Boson.Serve;
using Boson.Storage;
using Boson.Tests.Support;
using Boson.Util;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Boson.Tests;

public sealed class ManifestFlowOrchestratorTests : IDisposable
{
    private const string Repo = "acme/site";

    private readonly TempDb _db = new();
    private readonly TempDirs _dirs = new();
    private readonly ProjectsRepository _projects;
    private readonly DeploymentsRepository _deployments;
    private readonly PlatformRepository _platform;
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
        _deployments = new DeploymentsRepository(_db.Db);
        _platform = new PlatformRepository(_db.Db);
        _platform.Set(PlatformRepository.AdminHostname, "deploy.example.com");
        _orchestrator = new ManifestFlowOrchestrator(
            _projects, _deployments, _platform, _caddy, _git, _minter, _github, _dns,
            _dirs.Paths, _clock, NullLogger.Instance);

        // The fake fetch moves no files, so the checkout the flow will read has
        // to be there already, the way a real clone would have left it.
        WriteBosonFile("""
            version: 1
            deployments:
              - branch: main
                hostname: site.example.com
                aliases: [www.site.example.com]
                env: production
            """);
    }

    private void WriteBosonFile(string yaml, string branch = "main")
    {
        var dir = _dirs.Paths.CheckoutDir(Repo, DnsLabel.From(branch));

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, BosonFile.FileName), yaml);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dirs.Dispose();
    }

    private static AddRequest Request(string repo = Repo) => new(repo);

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

        // Nothing to warn about yet: what this project serves is in a file no
        // App exists to read.
        Assert.Empty(start.Warnings);
    }

    [Fact]
    public async Task Begin_rejects_a_repo_already_added()
    {
        _projects.Insert(TestProjects.New());

        await Assert.ThrowsAsync<BosonValidationException>(() =>
            _orchestrator.BeginAddAsync(Request()));
    }

    [Fact]
    public async Task Start_page_carries_the_manifest()
    {
        var start = await _orchestrator.BeginAddAsync(Request());
        var html = _orchestrator.RenderStartPage(start.Token)!;

        Assert.Contains("boson-site", html);

        // The App has to carry an address at creation, and no hostname is known
        // until the clone, so it starts on the control plane and moves later.
        Assert.Contains("https://deploy.example.com/_boson/webhook/acme/site", html);
        Assert.Contains("https://deploy.example.com/_boson/setup-app/callback", html);
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
    public async Task Installed_persists_the_project_its_deployments_and_moves_the_webhook()
    {
        var token = await RunToInstalledAsync();

        Assert.Equal("done", _orchestrator.GetStatus(token)!.Phase);

        var project = _projects.GetByRepo(Repo);
        Assert.NotNull(project);
        Assert.Equal(42, project.GithubAppId);
        Assert.Equal("boson-site", project.GithubAppSlug);
        Assert.Equal(777, project.GithubInstallationId);
        Assert.Equal("whsec_test", project.GithubWebhookSecret);

        var deployment = _deployments.GetByBranch(Repo, "main")!;
        Assert.Equal("site.example.com", deployment.Hostname);
        Assert.Equal(["www.site.example.com"], deployment.AliasList);
        Assert.Equal("production", deployment.EnvSet);
        Assert.False(deployment.WebhookActive);
        Assert.InRange(deployment.HostPort, HostPortAllocator.First, HostPortAllocator.Last);

        Assert.Equal("https://site.example.com/_boson/webhook/acme/site", _github.WebhookUrlSet);
        Assert.Equal(1, _caddy.SyncCalls);
        Assert.Equal(1, _git.Calls);
    }

    [Fact]
    public async Task The_environment_sets_the_file_names_come_back_for_the_admin_to_create()
    {
        var token = await RunToInstalledAsync();

        Assert.Equal(["production"], _orchestrator.GetStatus(token)!.EnvSets);
    }

    [Fact]
    public async Task The_default_branch_is_whatever_github_says_it_is()
    {
        _github.DefaultBranch = "trunk";

        WriteBosonFile("""
            version: 1
            deployments:
              - branch: trunk
                hostname: site.example.com
            """, branch: "trunk");

        await RunToInstalledAsync();

        Assert.NotNull(_deployments.GetByBranch(Repo, "trunk"));
    }

    [Fact]
    public async Task Webhook_moves_to_a_declared_hostname_when_default_branch_is_not_deployed()
    {
        _github.DefaultBranch = "trunk";

        WriteBosonFile("""
            version: 1
            deployments:
              - branch: production
                hostname: site.example.com
            """, branch: "trunk");

        await RunToInstalledAsync();

        Assert.Equal("https://site.example.com/_boson/webhook/acme/site", _github.WebhookUrlSet);
    }

    [Fact]
    public async Task A_file_of_only_patterns_leaves_the_webhook_where_it_is_and_says_so()
    {
        // No deployment exists to carry the address, and the ones that will
        // exist are made by pushes GitHub cannot deliver until it moves.
        WriteBosonFile("""
            version: 1
            deployments:
              - branch: "feature/*"
                hostname: "{branch}.preview.example.com"
            """);

        var token = await RunToInstalledAsync();

        Assert.Null(_github.WebhookUrlSet);
        Assert.Contains(_orchestrator.GetStatus(token)!.Warnings,
            w => w.Contains("webhook address is still deploy.example.com"));
    }

    [Fact]
    public async Task A_pattern_entry_makes_no_deployment_until_a_branch_matches_it()
    {
        WriteBosonFile("""
            version: 1
            deployments:
              - branch: main
                hostname: site.example.com
              - branch: "feature/*"
                hostname: "{branch}.preview.example.com"
            """);

        await RunToInstalledAsync();

        Assert.Single(_deployments.ListActiveForRepo(Repo));
    }

    [Fact]
    public async Task An_unresolvable_hostname_warns_without_blocking_the_add()
    {
        _dns.Resolves = false;

        var token = await RunToInstalledAsync();

        Assert.Equal("done", _orchestrator.GetStatus(token)!.Phase);
        Assert.Contains(_orchestrator.GetStatus(token)!.Warnings,
            w => w.Contains("site.example.com does not resolve"));
    }

    [Fact]
    public async Task A_hostname_on_no_local_interface_warns()
    {
        _dns.MatchesLocal = false;

        var token = await RunToInstalledAsync();

        Assert.Contains(_orchestrator.GetStatus(token)!.Warnings,
            w => w.Contains("no local interface"));
    }

    [Fact]
    public async Task A_webhook_address_that_will_not_move_is_reported_rather_than_lost()
    {
        _github.FailWebhookUrl = true;

        var token = await RunToInstalledAsync();

        Assert.Contains(_orchestrator.GetStatus(token)!.Warnings,
            w => w.Contains("pushes will not deploy"));
    }

    [Fact]
    public async Task Fetch_failure_leaves_the_project_added()
    {
        _git.Throw = new GitException("network down");
        var token = await RunToInstalledAsync();

        var status = _orchestrator.GetStatus(token)!;
        Assert.Equal("fetch_failed", status.Phase);
        Assert.Contains("network down", status.Error);
        Assert.NotNull(_projects.GetByRepo(Repo)); // boson deploy retries the fetch
    }

    [Fact]
    public async Task A_repository_with_no_boson_file_keeps_its_app_and_says_what_is_missing()
    {
        // The App's keys are issued once, so a file that does not read is a
        // push away from fixed and never a reason to throw them away.
        File.Delete(Path.Combine(
            _dirs.Paths.CheckoutDir(Repo, DnsLabel.From("main")), BosonFile.FileName));

        var token = await RunToInstalledAsync();

        Assert.Equal("fetch_failed", _orchestrator.GetStatus(token)!.Phase);
        Assert.Contains(BosonFile.FileName, _orchestrator.GetStatus(token)!.Error);
        Assert.NotNull(_projects.GetByRepo(Repo));
        Assert.Empty(_deployments.ListActiveForRepo(Repo));
    }

    [Fact]
    public async Task Failed_conversion_marks_the_flow_failed_and_persists_nothing()
    {
        _github.FailConversion = true;
        var start = await _orchestrator.BeginAddAsync(Request());
        var redirect = await _orchestrator.HandleCallbackAsync(start.Token, "code-1");

        Assert.Null(redirect);
        Assert.Equal("failed", _orchestrator.GetStatus(start.Token)!.Phase);
        Assert.Null(_projects.GetByRepo(Repo));
    }

    [Fact]
    public async Task Abandoned_flow_persists_nothing()
    {
        var start = await _orchestrator.BeginAddAsync(Request());
        await _orchestrator.HandleCallbackAsync(start.Token, "code-1");
        // Browser never reaches /installed.
        Assert.Null(_projects.GetByRepo(Repo));
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
