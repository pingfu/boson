using System.Text.Json;
using Boson.Deploy;
using Boson.Github;
using Boson.Serve;
using Boson.Storage;
using Boson.Tests.Support;
using Boson.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Boson.Tests;

public sealed class DeployEndpointTests : IAsyncLifetime
{
    private const string Repo = "acme/site";
    private const string Url = "/deploy/acme/site";
    private const string StatusUrl = "/declare/status/acme/site";

    private const string OneBranch = """
        version: 1
        deployments:
          - branch: main
            hostname: site.example.com
        """;

    private readonly TempDb _db = new();
    private readonly TempDirs _dirs = new();
    private readonly FakeGitCli _git = new();
    private readonly FakeMinter _minter = new();
    private readonly FakeGithubClient _github = new();
    private readonly FakeCaddySynchroniser _caddy = new();
    private readonly FakeDnsResolver _dns = new();
    private readonly FakeDeployer _deployer = new();
    private ProjectsRepository _projects = null!;
    private DeploymentsRepository _deployments = null!;
    private ManifestFlowOrchestrator _orchestrator = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _projects = new ProjectsRepository(_db.Db);
        _deployments = new DeploymentsRepository(_db.Db);

        var platform = new PlatformRepository(_db.Db);
        platform.Set(PlatformRepository.AdminHostname, "deploy.example.com");

        _orchestrator = new ManifestFlowOrchestrator(
            _projects, _deployments, platform, _caddy, _git, _minter, _github, _dns,
            _dirs.Paths, TimeProvider.System, NullLogger.Instance);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        DeployEndpoint.Map(_app, _projects, _deployments, _deployer, _orchestrator, NullLogger.Instance);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _db.Dispose();
        _dirs.Dispose();
    }

    /// <summary>
    /// The fake fetch moves no files, so the checkout the retry will read has
    /// to be there already, the way a real clone would have left it.
    /// </summary>
    private void WriteBosonFile(string yaml)
    {
        var dir = _dirs.Paths.CheckoutDir(Repo, BranchSlug.From("main"));

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, BosonFile.FileName), yaml);
    }

    private async Task<(int Status, string Body)> DeployAsync(string url = Url)
    {
        var response = await _client.PostAsync(url, content: null);

        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<DeployStartResponse> StartAsync()
    {
        var (status, body) = await DeployAsync();

        Assert.Equal(202, status);

        return JsonSerializer.Deserialize(body, RpcJson.Default.DeployStartResponse)!;
    }

    /// <summary>Null when the daemon holds no declaration for the repository.</summary>
    private async Task<DeclareStatusResponse?> StatusAsync()
    {
        var response = await _client.GetAsync(StatusUrl);

        return response.IsSuccessStatusCode
            ? JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(), RpcJson.Default.DeclareStatusResponse)
            : null;
    }

    /// <summary>What `boson deploy` does: ask, follow the declaration to its end, report it.</summary>
    private async Task<DeclareStatusResponse> DeclareAsync()
    {
        Assert.True((await StartAsync()).Declaring);

        await _orchestrator.TryGetDeclaration(Repo)!;

        return (await StatusAsync())!;
    }

    [Fact]
    public async Task A_project_with_no_deployments_is_declared_and_then_deployed()
    {
        // What `boson add` leaves when the App is saved and the first fetch fails.
        _projects.Insert(TestProjects.New(repo: Repo));
        WriteBosonFile(OneBranch);

        var declared = await DeclareAsync();

        Assert.Equal("done", declared.Phase);
        Assert.Empty(declared.Warnings);
        Assert.Equal("site.example.com", _deployments.GetByBranch(Repo, "main")!.Hostname);
        Assert.Equal(1, _caddy.SyncCalls);
        Assert.Equal("https://site.example.com/_boson/webhook/acme/site", _github.WebhookUrlSet);
        Assert.Empty(_deployer.Calls);

        var deploy = await StartAsync();

        Assert.False(deploy.Declaring);
        Assert.Equal(1, deploy.DeployId);
        Assert.Equal([(Repo, "main", DeployTrigger.Manual)], _deployer.Calls);
    }

    [Fact]
    public async Task The_request_returns_while_the_clone_is_still_running()
    {
        _projects.Insert(TestProjects.New(repo: Repo));
        WriteBosonFile(OneBranch);

        var clone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _git.OnFetch = _ => clone.Task;

        Assert.True((await StartAsync()).Declaring);
        Assert.Equal("declaring", (await StatusAsync())!.Phase);

        clone.SetResult();
        await _orchestrator.TryGetDeclaration(Repo)!;

        Assert.Equal("done", (await StatusAsync())!.Phase);
    }

    [Fact]
    public async Task A_second_deploy_joins_the_declaration_in_flight()
    {
        _projects.Insert(TestProjects.New(repo: Repo));
        WriteBosonFile(OneBranch);

        var clone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _git.OnFetch = _ => clone.Task;

        Assert.True((await StartAsync()).Declaring);

        var first = _orchestrator.TryGetDeclaration(Repo);

        Assert.True((await StartAsync()).Declaring);
        Assert.Same(first, _orchestrator.TryGetDeclaration(Repo));

        clone.SetResult();
        await first!;

        Assert.Equal(1, _git.Calls);
        Assert.Single(_deployments.ListActiveForRepo(Repo));
    }

    [Fact]
    public async Task Warnings_from_declaring_come_back_for_the_cli_to_print()
    {
        _projects.Insert(TestProjects.New(repo: Repo));
        WriteBosonFile(OneBranch);
        _github.FailWebhookUrl = true;
        _dns.Resolves = false;

        var declared = await DeclareAsync();

        Assert.Equal("done", declared.Phase);
        Assert.Contains(declared.Warnings, w => w.Contains("pushes will not deploy"));
        Assert.Contains(declared.Warnings, w => w.Contains("site.example.com does not resolve"));
    }

    [Fact]
    public async Task A_project_that_already_has_deployments_is_deployed_without_declaring_again()
    {
        TestProjects.Insert(_projects, _deployments, repo: Repo);

        Assert.False((await StartAsync()).Declaring);
        Assert.Equal(0, _git.Calls);
        Assert.Null(_github.WebhookUrlSet);
        Assert.Single(_deployer.Calls);
        Assert.Null(await StatusAsync());
    }

    [Fact]
    public async Task A_repository_the_installation_cannot_read_fails_saying_so()
    {
        _projects.Insert(TestProjects.New(repo: Repo));
        _github.FailDefaultBranch = new GithubApiException("could not read acme/site (404): Not Found", 404);

        var declared = await DeclareAsync();

        Assert.Equal("failed", declared.Phase);
        Assert.Contains("boson-site cannot read acme/site", declared.Error);
        Assert.Contains("was not granted this repository", declared.Error);
        Assert.Contains("could not read acme/site (404): Not Found", declared.Error);
        Assert.Empty(_deployments.ListActiveForRepo(Repo));
        Assert.Empty(_deployer.Calls);
    }

    [Fact]
    public async Task A_repository_with_no_boson_file_fails_naming_the_file()
    {
        _projects.Insert(TestProjects.New(repo: Repo));

        var declared = await DeclareAsync();

        Assert.Equal("failed", declared.Phase);
        Assert.Contains($"no {BosonFile.FileName} at the root of {Repo}", declared.Error);
        Assert.Empty(_deployer.Calls);
    }

    [Fact]
    public async Task A_failed_declaration_is_tried_again_by_the_next_deploy()
    {
        _projects.Insert(TestProjects.New(repo: Repo));
        _git.Throw = new GitException("network down");

        var failed = await DeclareAsync();

        Assert.Equal("failed", failed.Phase);
        Assert.Contains("network down", failed.Error);

        _git.Throw = null;
        WriteBosonFile(OneBranch);

        Assert.Equal("done", (await DeclareAsync()).Phase);
        Assert.NotNull(_deployments.GetByBranch(Repo, "main"));
    }

    [Fact]
    public async Task A_declaration_that_throws_partway_leaves_nothing_half_declared()
    {
        // The second hostname belongs to another project, so the insert that
        // follows a good one throws.
        TestProjects.Insert(_projects, _deployments, repo: "acme/other", hostname: "taken.example.com");
        _projects.Insert(TestProjects.New(repo: Repo));

        WriteBosonFile("""
            version: 1
            deployments:
              - branch: main
                hostname: site.example.com
              - branch: staging
                hostname: taken.example.com
            """);

        var declared = await DeclareAsync();

        Assert.Equal("failed", declared.Phase);
        Assert.Empty(_deployments.ListActiveForRepo(Repo));
        Assert.Null(_github.WebhookUrlSet);

        // With nothing left behind, the next deploy declares the whole file.
        WriteBosonFile(OneBranch);

        Assert.Equal("done", (await DeclareAsync()).Phase);
        Assert.Equal("https://site.example.com/_boson/webhook/acme/site", _github.WebhookUrlSet);
    }

    [Fact]
    public async Task A_file_of_only_patterns_has_nothing_to_deploy_and_says_why()
    {
        _projects.Insert(TestProjects.New(repo: Repo));

        WriteBosonFile("""
            version: 1
            deployments:
              - branch: "feature/*"
                hostname: "{branch}.preview.example.com"
            """);

        var declared = await DeclareAsync();

        Assert.Equal("failed", declared.Phase);
        Assert.Contains("names no branch outright", declared.Error);
        Assert.Contains(declared.Warnings, w => w.Contains("webhook address is still deploy.example.com"));
        Assert.Empty(_deployer.Calls);
    }

    [Fact]
    public async Task An_unknown_repository_is_reported_as_unknown()
    {
        var (status, body) = await DeployAsync("/deploy/acme/nope");

        Assert.Equal(404, status);
        Assert.Contains("unknown project: acme/nope", body);
        Assert.Equal(0, _git.Calls);
    }

    [Fact]
    public async Task A_branch_with_no_deployment_is_refused_by_name()
    {
        TestProjects.Insert(_projects, _deployments, repo: Repo);

        var (status, body) = await DeployAsync($"{Url}?branch=staging");

        Assert.Equal(404, status);
        Assert.Contains("acme/site has no deployment for staging", body);
    }
}
