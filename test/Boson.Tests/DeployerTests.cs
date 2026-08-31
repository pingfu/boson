using Boson.Deploy;
using Boson.Storage;
using Boson.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Boson.Tests;

public class DeployerTests : IDisposable
{
    private const string Repo = "acme/site";

    private readonly TempDb _db = new();
    private readonly TempDirs _dirs = new();
    private readonly ProjectsRepository _projects;
    private readonly DeploysRepository _deploys;
    private readonly ProjectLocks _locks = new();
    private readonly FakeGitCli _git = new();
    private readonly FakeDockerCli _docker = new();
    private readonly FakeMinter _minter = new();

    public DeployerTests()
    {
        _projects = new ProjectsRepository(_db.Db);
        _deploys = new DeploysRepository(_db.Db);
        _projects.Insert(TestProjects.New());
    }

    private Deployer NewDeployer() => new(
        _projects, _deploys, _minter, _git, _docker, _locks, _dirs.Paths, NullLogger.Instance);

    public void Dispose()
    {
        _db.Dispose();
        _dirs.Dispose();
    }

    [Fact]
    public async Task Unknown_project_returns_not_found()
    {
        var result = await NewDeployer().DeployAsync("acme/nope", DeployTrigger.Manual);
        Assert.IsType<DeployResult.NotFound>(result);
    }

    [Fact]
    public async Task Manual_deploy_on_held_lock_returns_lock_held_without_a_row()
    {
        _locks.TryEnter(Repo);
        var result = await NewDeployer().DeployAsync(Repo, DeployTrigger.Manual);

        Assert.IsType<DeployResult.LockHeld>(result);
        Assert.Empty(_deploys.ListForProject(_projects.GetByRepo(Repo)!.Id));
        Assert.False(_projects.GetByRepo(Repo)!.DeployPending);
    }

    [Fact]
    public async Task Webhook_deploy_on_held_lock_coalesces_into_deploy_pending()
    {
        _locks.TryEnter(Repo);
        var result = await NewDeployer().DeployAsync(Repo, DeployTrigger.Webhook);

        Assert.IsType<DeployResult.Coalesced>(result);
        Assert.True(_projects.GetByRepo(Repo)!.DeployPending);
        Assert.Empty(_deploys.ListForProject(_projects.GetByRepo(Repo)!.Id));
    }

    [Fact]
    public async Task Successful_deploy_records_row_and_activates_webhook()
    {
        long? startedId = null;
        var result = await NewDeployer().DeployAsync(
            Repo, DeployTrigger.Manual, id => startedId = id);

        var completed = Assert.IsType<DeployResult.Completed>(result);
        Assert.True(completed.Succeeded);
        Assert.Equal(1, completed.Passes);
        Assert.Equal(completed.LastDeployId, startedId);

        var row = _deploys.Get(completed.LastDeployId)!;
        Assert.Equal(DeployStatus.Succeeded, row.Status);
        Assert.Equal(DeployTrigger.Manual, row.Trigger);
        Assert.NotNull(row.CommitSha);
        Assert.NotNull(row.FinishedAt);
        Assert.True(File.Exists(row.LogPath));
        Assert.True(_projects.GetByRepo(Repo)!.WebhookActive);
        Assert.False(_locks.IsHeld(Repo));
    }

    [Fact]
    public async Task Failed_compose_leaves_project_inactive_with_error_recorded()
    {
        _docker.UpExitCode = 1;
        var result = await NewDeployer().DeployAsync(Repo, DeployTrigger.Manual);

        var completed = Assert.IsType<DeployResult.Completed>(result);
        Assert.False(completed.Succeeded);
        var row = _deploys.Get(completed.LastDeployId)!;
        Assert.Equal(DeployStatus.Failed, row.Status);
        Assert.Contains("docker compose up exited 1", row.Error);
        Assert.False(_projects.GetByRepo(Repo)!.WebhookActive);
    }

    [Fact]
    public async Task Token_mint_failure_is_recorded_with_its_prefix()
    {
        _minter.Fail = true;
        var result = await NewDeployer().DeployAsync(Repo, DeployTrigger.Manual);

        var completed = Assert.IsType<DeployResult.Completed>(result);
        Assert.False(completed.Succeeded);
        Assert.StartsWith("token mint:", _deploys.Get(completed.LastDeployId)!.Error);
    }

    [Fact]
    public async Task Drain_loops_exactly_once_for_a_single_mid_deploy_push()
    {
        // A push lands during pass 1 only.
        _git.OnFetch = call =>
        {
            if (call == 1) _projects.SetDeployPending(Repo, true);
            return Task.CompletedTask;
        };

        var result = await NewDeployer().DeployAsync(Repo, DeployTrigger.Manual);

        var completed = Assert.IsType<DeployResult.Completed>(result);
        Assert.Equal(2, completed.Passes);
        var rows = _deploys.ListForProject(_projects.GetByRepo(Repo)!.Id);
        Assert.Equal(2, rows.Count);
        Assert.Equal(DeployTrigger.Manual, rows[0].Trigger);
        Assert.Equal(DeployTrigger.Webhook, rows[1].Trigger);
        Assert.False(_projects.GetByRepo(Repo)!.DeployPending);
    }

    [Fact]
    public async Task Three_pass_bound_holds_under_a_push_flood_and_leaves_pending_set()
    {
        // Every pass sees a fresh push.
        _git.OnFetch = _ =>
        {
            _projects.SetDeployPending(Repo, true);
            return Task.CompletedTask;
        };

        var result = await NewDeployer().DeployAsync(Repo, DeployTrigger.Manual);

        var completed = Assert.IsType<DeployResult.Completed>(result);
        Assert.Equal(Deployer.MaxPasses, completed.Passes);
        Assert.Equal(Deployer.MaxPasses,
            _deploys.ListForProject(_projects.GetByRepo(Repo)!.Id).Count);
        Assert.True(_projects.GetByRepo(Repo)!.DeployPending);
        Assert.False(_locks.IsHeld(Repo));
    }

    [Fact]
    public async Task Drain_runs_even_after_a_failed_deploy()
    {
        // A newer commit is often the fix for a broken one (spec §6 step 9).
        _docker.UpExitCode = 1;
        _git.OnFetch = call =>
        {
            if (call == 1) _projects.SetDeployPending(Repo, true);
            return Task.CompletedTask;
        };

        var result = await NewDeployer().DeployAsync(Repo, DeployTrigger.Manual);

        var completed = Assert.IsType<DeployResult.Completed>(result);
        Assert.Equal(2, completed.Passes);
        Assert.False(_projects.GetByRepo(Repo)!.DeployPending);
    }

    [Fact]
    public async Task Deploy_on_inactive_project_runs_normally()
    {
        // Activation filtering guards only the webhook entry path (spec §6).
        Assert.False(_projects.GetByRepo(Repo)!.WebhookActive);
        var result = await NewDeployer().DeployAsync(Repo, DeployTrigger.Webhook);
        Assert.IsType<DeployResult.Completed>(result);
    }
}
