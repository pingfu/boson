using Boson.Deploy;
using Boson.Storage;
using Boson.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Boson.Tests;

public class DeployerTests : IDisposable
{
    private const string Repo = TestProjects.Repo;
    private const string Branch = TestProjects.Branch;

    private readonly TempDb _db = new();
    private readonly TempDirs _dirs = new();
    private readonly ProjectsRepository _projects;
    private readonly DeploymentsRepository _deployments;
    private readonly DeploysRepository _deploys;
    private readonly PlatformRepository _platform;
    private readonly DeploymentLocks _locks = new();
    private readonly FakeGitCli _git = new();
    private readonly FakeDockerCli _docker = new();
    private readonly ImageRetainer _imageRetainer;
    private readonly FakeMinter _minter = new();
    private readonly FakeCaddySynchroniser _caddy = new();
    private readonly Deployment _deployment;

    public DeployerTests()
    {
        _projects = new ProjectsRepository(_db.Db);
        _deployments = new DeploymentsRepository(_db.Db);
        _deploys = new DeploysRepository(_db.Db);
        _platform = new PlatformRepository(_db.Db);
        _platform.Set(PlatformRepository.AdminHostname, "boson.example.com");
        _imageRetainer = new ImageRetainer(_docker);

        _deployment = TestProjects.Insert(_projects, _deployments);

        WriteBosonFile($"""
            version: 1
            deployments:
              - branch: {Branch}
                hostname: {TestProjects.Hostname}
            """);
    }

    /// <summary>A deploy reads the file the fetch left behind, so a checkout without one has nothing to deploy.</summary>
    private void WriteBosonFile(string yaml, string branch = Branch)
    {
        var dir = _dirs.Paths.CheckoutDir(Repo, BranchSlug.From(branch));

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, BosonFile.FileName), yaml);
    }

    private Deployer NewDeployer() => new(
        _projects, _deployments, _deploys, _platform, _minter, _git, _docker, _imageRetainer, _caddy, _locks,
        _dirs.Paths, NullLogger.Instance);

    private Deployment Current() => _deployments.GetByBranch(Repo, Branch)!;

    private IReadOnlyList<DeployRow> Rows() => _deploys.ListForDeployment(_deployment.Id);

    public void Dispose()
    {
        _db.Dispose();
        _dirs.Dispose();
    }

    [Fact]
    public async Task Unknown_project_returns_not_found()
    {
        var result = await NewDeployer().DeployAsync("acme/nope", Branch, DeployTrigger.Manual);
        Assert.IsType<DeployResult.NotFound>(result);
    }

    [Fact]
    public async Task Manual_deploy_on_held_lock_returns_lock_held_without_a_row()
    {
        _locks.TryEnter(Deployer.LockKey(Repo, Branch));
        var result = await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        Assert.IsType<DeployResult.LockHeld>(result);
        Assert.Empty(Rows());
        Assert.False(Current().DeployPending);
    }

    [Fact]
    public async Task A_lock_on_one_branch_does_not_hold_another()
    {
        // Two branches of a repository are separate containers on separate
        // ports, with nothing to serialise between them.
        _locks.TryEnter(Deployer.LockKey(Repo, "develop"));

        var result = await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        Assert.IsType<DeployResult.Completed>(result);
    }

    [Fact]
    public async Task Webhook_deploy_on_held_lock_coalesces_into_deploy_pending()
    {
        _locks.TryEnter(Deployer.LockKey(Repo, Branch));
        var result = await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Webhook);

        Assert.IsType<DeployResult.Coalesced>(result);
        Assert.True(Current().DeployPending);
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task Successful_deploy_records_row_and_activates_webhook()
    {
        long? startedId = null;
        var result = await NewDeployer().DeployAsync(
            Repo, Branch, DeployTrigger.Manual, id => startedId = id);

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
        Assert.True(Current().WebhookActive);
        Assert.False(_locks.IsHeld(Deployer.LockKey(Repo, Branch)));
    }

    [Fact]
    public async Task The_commit_is_the_image_tag_compose_builds_with()
    {
        var result = await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        var completed = Assert.IsType<DeployResult.Completed>(result);
        var sha = _deploys.Get(completed.LastDeployId)!.CommitSha;

        Assert.Equal(sha, _docker.VariablesPassed[^1].Commit);
    }

    [Fact]
    public async Task The_branch_slug_reaches_compose_so_paths_can_vary_per_branch()
    {
        // Without it a compose file mounting a fixed host path gives two
        // branches one directory, and staging writes into production's data.
        await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        Assert.Equal(_deployment.BranchSlug, _docker.VariablesPassed[^1].BranchSlug);
    }

    [Fact]
    public async Task Failed_compose_leaves_the_deployment_inactive_with_error_recorded()
    {
        _docker.UpExitCode = 1;
        var result = await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        var completed = Assert.IsType<DeployResult.Completed>(result);
        Assert.False(completed.Succeeded);
        var row = _deploys.Get(completed.LastDeployId)!;
        Assert.Equal(DeployStatus.Failed, row.Status);
        Assert.Contains("docker compose up exited 1", row.Error);
        Assert.False(Current().WebhookActive);
    }

    [Fact]
    public async Task A_checkout_with_no_boson_file_has_nothing_to_deploy()
    {
        File.Delete(Path.Combine(
            _dirs.Paths.CheckoutDir(Repo, BranchSlug.From(Branch)), BosonFile.FileName));

        Assert.Contains($"no {BosonFile.FileName}", await FailureAsync());
        Assert.Equal(0, _docker.UpCalls);
    }

    [Fact]
    public async Task A_file_that_no_longer_declares_the_branch_stops_the_deploy()
    {
        WriteBosonFile("""
            version: 1
            deployments:
              - branch: some-other-branch
                hostname: other.example.com
            """);

        Assert.Contains($"no longer declares {Branch}", await FailureAsync());
        Assert.Equal(0, _docker.UpCalls);
    }

    [Fact]
    public async Task A_branch_nothing_declares_is_not_deployed_and_leaves_no_checkout()
    {
        WriteBosonFile("""
            version: 1
            deployments:
              - branch: main
                hostname: site.example.com
            """, branch: "scratch");

        var result = await NewDeployer().DeployAsync(Repo, "scratch", DeployTrigger.Webhook);

        Assert.IsType<DeployResult.NotDeclared>(result);
        Assert.False(Directory.Exists(_dirs.Paths.CheckoutDir(Repo, BranchSlug.From("scratch"))));
    }

    [Fact]
    public async Task A_matched_branch_gets_its_own_deployment_on_first_push()
    {
        WriteBosonFile("""
            version: 1
            deployments:
              - branch: main
                hostname: site.example.com
              - branch: "feature/*"
                hostname: "{branch}.preview.example.com"
                expire_after: 7d
            """, branch: "feature/search");

        var result = await NewDeployer().DeployAsync(Repo, "feature/search", DeployTrigger.Webhook);

        Assert.IsType<DeployResult.Completed>(result);

        var created = _deployments.GetByBranch(Repo, "feature/search")!;

        Assert.Equal($"{BranchSlug.From("feature/search")}.preview.example.com", created.Hostname);
        Assert.Equal("7d", created.ExpireAfter);
        Assert.NotEqual(_deployment.Port, created.Port);
        Assert.True(_caddy.SyncCalls > 0);
    }

    [Fact]
    public async Task A_changed_hostname_moves_the_deployment_and_the_routing()
    {
        // The file is the authority: a rename is applied, not reported.
        WriteBosonFile($"""
            version: 1
            deployments:
              - branch: {Branch}
                hostname: renamed.example.com
                aliases: [www.renamed.example.com]
            """);

        var result = await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        Assert.IsType<DeployResult.Completed>(result);
        Assert.Equal("renamed.example.com", Current().Hostname);
        Assert.Equal(["www.renamed.example.com"], Current().AliasList);
        Assert.True(_caddy.SyncCalls > 0);
    }

    [Fact]
    public async Task A_named_env_set_with_no_file_on_the_host_stops_the_deploy()
    {
        WriteBosonFile($"""
            version: 1
            deployments:
              - branch: {Branch}
                hostname: {TestProjects.Hostname}
                env: production
            """);

        var error = await FailureAsync();

        Assert.Contains("production", error);
        Assert.Contains(_dirs.Paths.EnvFile(Repo, "production"), error);
    }

    [Fact]
    public async Task The_named_env_set_reaches_compose()
    {
        var envFile = _dirs.Paths.EnvFile(Repo, "production");

        Directory.CreateDirectory(_dirs.Paths.EnvDir(Repo));
        File.WriteAllText(envFile, "KEY=value\n");

        WriteBosonFile($"""
            version: 1
            deployments:
              - branch: {Branch}
                hostname: {TestProjects.Hostname}
                env: production
            """);

        await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        Assert.Equal(envFile, _docker.VariablesPassed[^1].EnvFilePath);
    }

    private const string Sha2 = "0000000200000000000000000000000000000000";
    private const string Sha3 = "0000000300000000000000000000000000000000";
    private const string Sha4 = "0000000400000000000000000000000000000000";

    /// <summary>A deploy of this commit that worked, the way history records one.</summary>
    private void Deployed(Deployment deployment, string sha)
    {
        var id = _deploys.Insert(deployment.Id, DeployTrigger.Webhook, i => $"{i}.log");

        _deploys.SetCommitSha(id, sha);
        _deploys.Finish(id, succeeded: true, error: null);
    }

    [Fact]
    public async Task Retention_runs_once_the_deploy_has_succeeded()
    {
        // What it refuses to touch is ImageRetainerTests; this is that a
        // successful deploy reaches it with the commits this deployment has
        // outlived and the count the file declared.
        Deployed(_deployment, Sha4);
        Deployed(_deployment, Sha3);
        Deployed(_deployment, Sha2);

        _docker.ImageTags["acme/site-web"] = [_git.NextSha, Sha2, Sha3, Sha4];

        await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        Assert.Equal([$"acme/site-web:{Sha4}"], _docker.ImagesRemoved);
    }

    [Fact]
    public async Task Retention_leaves_another_branchs_commits_alone()
    {
        // Two branches building one image name share its tags. Sha4 is on
        // disk and is develop's, and this branch never deployed it.
        Deployed(_deployment, Sha3);
        Deployed(_deployment, Sha2);

        var develop = TestProjects.Insert(
            _projects, _deployments, branch: "develop", hostname: "develop.example.com", port: 30001);

        Deployed(develop, Sha4);

        _docker.ImageTags["acme/site-web"] = [_git.NextSha, Sha2, Sha3, Sha4];

        await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        Assert.Empty(_docker.ImagesRemoved);
    }

    [Fact]
    public async Task A_admin_hostname_spelled_out_for_another_branch_stops_this_one_too()
    {
        // Refused for the file, not for the branch, so it is caught by
        // whichever deploy comes first and by `boson add` before any.
        _platform.Set(PlatformRepository.AdminHostname, "control.example.com");

        WriteBosonFile($"""
            version: 1
            deployments:
              - branch: {Branch}
                hostname: {TestProjects.Hostname}
              - branch: "preview/*"
                hostname: "{BranchPlaceholder}.example.com"
                aliases: [control.example.com]
            """);

        Assert.Contains("admin hostname", await FailureAsync());
        Assert.Equal(0, _docker.UpCalls);
    }

    private const string BranchPlaceholder = "{branch}";

    [Fact]
    public async Task A_hostname_another_repository_serves_fails_the_deploy_naming_who_has_it()
    {
        TestProjects.Insert(
            _projects, _deployments, repo: "acme/other", hostname: "taken.example.com", port: 30001);

        WriteBosonFile($"""
            version: 1
            deployments:
              - branch: {Branch}
                hostname: taken.example.com
            """);

        // A failed deploy on this deployment's history, where `boson status`
        // shows it, rather than a crashed task and a line in the daemon log.
        Assert.Contains("taken.example.com is already served by acme/other branch main", await FailureAsync());
        Assert.Equal(0, _docker.UpCalls);
        Assert.Equal(TestProjects.Hostname, Current().Hostname);
        Assert.False(_locks.IsHeld(Deployer.LockKey(Repo, Branch)));
    }

    [Fact]
    public async Task An_alias_another_deployment_serves_is_a_taken_name_too()
    {
        TestProjects.Insert(
            _projects, _deployments, repo: "acme/other", hostname: "other.example.com",
            aliases: "www.other.example.com", port: 30001);

        WriteBosonFile($"""
            version: 1
            deployments:
              - branch: {Branch}
                hostname: {TestProjects.Hostname}
                aliases: [WWW.other.example.com]
            """);

        Assert.Contains("www.other.example.com is already served by acme/other", await FailureAsync());
    }

    [Fact]
    public async Task A_new_branch_whose_hostname_is_taken_ends_without_a_deployment_or_a_crash()
    {
        var slug = BranchSlug.From("feature/search");

        TestProjects.Insert(
            _projects, _deployments, repo: "acme/other", hostname: $"{slug}.preview.example.com", port: 30001);

        WriteBosonFile("""
            version: 1
            deployments:
              - branch: "feature/*"
                hostname: "{branch}.preview.example.com"
            """, branch: "feature/search");

        var result = await NewDeployer().DeployAsync(Repo, "feature/search", DeployTrigger.Webhook);

        // No deployment to record it against, so the reason comes back to
        // whoever asked.
        Assert.Contains("is already served by acme/other", Assert.IsType<DeployResult.Stopped>(result).Error);
        Assert.Null(_deployments.GetByBranch(Repo, "feature/search"));
        Assert.Equal(0, _docker.UpCalls);
    }

    [Fact]
    public async Task A_pattern_that_generates_the_admin_hostname_stops_that_branch()
    {
        // The one name that cannot be checked before the branch exists.
        _platform.Set(PlatformRepository.AdminHostname, "boson.example.com");

        WriteBosonFile("""
            version: 1
            deployments:
              - branch: "*"
                hostname: "{branch}.example.com"
            """, branch: "boson");

        var result = await NewDeployer().DeployAsync(Repo, "boson", DeployTrigger.Webhook);

        Assert.Contains("admin hostname", Assert.IsType<DeployResult.Stopped>(result).Error);
        Assert.Null(_deployments.GetByBranch(Repo, "boson"));
    }

    [Fact]
    public async Task A_boson_file_that_cannot_be_opened_is_a_failed_deploy_with_a_reason()
    {
        var path = Path.Combine(_dirs.Paths.CheckoutDir(Repo, BranchSlug.From(Branch)), BosonFile.FileName);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Contains($"{BosonFile.FileName} could not be read", await FailureAsync());

        Assert.Equal(0, _docker.UpCalls);
    }

    [Fact]
    public async Task A_caddy_timeout_is_a_failed_deploy_and_not_a_cancellation()
    {
        // HttpClient reports a timeout as a cancelled task. Nobody cancelled
        // this deploy, and treated as cancelled it would skip the put-back.
        WriteBosonFile($"""
            version: 1
            deployments:
              - branch: {Branch}
                hostname: renamed.example.com
            """);

        _caddy.Throw = new TaskCanceledException("the request timed out", new TimeoutException());

        Assert.Contains("caddy did not take the change to renamed.example.com", await FailureAsync());
        Assert.Equal(TestProjects.Hostname, Current().Hostname);

        _caddy.Throw = null;
        var synced = _caddy.SyncCalls;

        Assert.True(Assert.IsType<DeployResult.Completed>(
            await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual)).Succeeded);
        Assert.True(_caddy.SyncCalls > synced);
    }

    [Fact]
    public async Task A_change_that_cannot_be_put_back_is_still_a_failed_deploy()
    {
        WriteBosonFile($"""
            version: 1
            deployments:
              - branch: {Branch}
                hostname: renamed.example.com
            """);

        // While Caddy is being asked, another repository's first deploy takes
        // the name this one has just let go of, and then Caddy refuses.
        _caddy.OnSync = () =>
        {
            _caddy.OnSync = null;
            TestProjects.Insert(
                _projects, _deployments, repo: "acme/other", hostname: TestProjects.Hostname, port: 30001);
        };
        _caddy.Throw = new HttpRequestException("connection refused");

        var deployer = NewDeployer();

        var failed = Assert.IsType<DeployResult.Completed>(
            await deployer.DeployAsync(Repo, Branch, DeployTrigger.Manual));

        Assert.False(failed.Succeeded);
        Assert.Contains("caddy did not take the change", _deploys.Get(failed.LastDeployId)!.Error);
        Assert.Equal("renamed.example.com", Current().Hostname);
    }

    [Fact]
    public async Task The_image_of_a_deploy_that_failed_is_left_for_the_operator_to_prune()
    {
        _docker.UpExitCode = 1;
        await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);
        var failedSha = Rows()[^1].CommitSha!;

        _docker.UpExitCode = 0;
        _docker.ImageTags["acme/site-web"] = [_git.NextSha, failedSha];

        await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        Assert.Empty(_docker.ImagesRemoved);
    }

    [Fact]
    public async Task A_commit_deployed_twice_counts_once_towards_what_is_kept()
    {
        Deployed(_deployment, Sha4);
        Deployed(_deployment, Sha3);
        Deployed(_deployment, Sha3);
        Deployed(_deployment, Sha2);

        _docker.ImageTags["acme/site-web"] = [_git.NextSha, Sha2, Sha3, Sha4];

        await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        // keep: 3 is this deploy's commit, Sha2 and Sha3. A redeploy of Sha3
        // is one image, not two of the allowance.
        Assert.Equal([$"acme/site-web:{Sha4}"], _docker.ImagesRemoved);
    }

    [Fact]
    public async Task A_route_caddy_refuses_fails_the_deploy_and_is_tried_again_by_the_next()
    {
        WriteBosonFile($"""
            version: 1
            deployments:
              - branch: {Branch}
                hostname: renamed.example.com
            """);

        _caddy.Throw = new HttpRequestException("caddy rejected config load (400)");

        Assert.Contains("caddy did not take the change to renamed.example.com", await FailureAsync());
        Assert.Equal(0, _docker.UpCalls);

        // Put back, so the change is still a change the next time.
        Assert.Equal(TestProjects.Hostname, Current().Hostname);

        _caddy.Throw = null;
        var synced = _caddy.SyncCalls;

        var result = await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        Assert.True(Assert.IsType<DeployResult.Completed>(result).Succeeded);
        Assert.Equal("renamed.example.com", Current().Hostname);
        Assert.True(_caddy.SyncCalls > synced);
    }

    [Fact]
    public async Task A_new_branch_caddy_refuses_is_withdrawn_so_the_next_push_declares_it_again()
    {
        WriteBosonFile("""
            version: 1
            deployments:
              - branch: "feature/*"
                hostname: "{branch}.preview.example.com"
            """, branch: "feature/search");

        _caddy.Throw = new HttpRequestException("connection refused");

        var result = await NewDeployer().DeployAsync(Repo, "feature/search", DeployTrigger.Webhook);

        Assert.Contains("caddy did not take the route", Assert.IsType<DeployResult.Stopped>(result).Error);
        Assert.Null(_deployments.GetByBranch(Repo, "feature/search"));
        Assert.Equal(0, _docker.UpCalls);

        _caddy.Throw = null;

        result = await NewDeployer().DeployAsync(Repo, "feature/search", DeployTrigger.Webhook);

        Assert.True(Assert.IsType<DeployResult.Completed>(result).Succeeded);
        Assert.NotNull(_deployments.GetByBranch(Repo, "feature/search"));
    }

    [Fact]
    public async Task A_file_with_an_empty_section_is_a_failed_deploy_with_a_reason()
    {
        WriteBosonFile("version: 1\ndeployments:\n");

        Assert.Contains("declares no deployments", await FailureAsync());
    }

    [Fact]
    public async Task Retention_leaves_tags_this_deployment_never_deployed()
    {
        // Counting the tags on disk would call these three this branch's
        // history and delete the oldest, whoever built it.
        _docker.ImageTags["acme/site-web"] = [_git.NextSha, Sha2, Sha3, Sha4];

        await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        Assert.Empty(_docker.ImagesRemoved);
    }

    [Fact]
    public async Task A_deployment_cannot_take_the_admin_hostname()
    {
        _platform.Set(PlatformRepository.AdminHostname, "control.example.com");

        WriteBosonFile($"""
            version: 1
            deployments:
              - branch: {Branch}
                hostname: {TestProjects.Hostname}
                aliases: [Control.example.com]
            """);

        Assert.Contains("admin hostname", await FailureAsync());
        Assert.Equal(0, _docker.UpCalls);
        Assert.Empty(Current().AliasList);
    }

    [Fact]
    public async Task Retention_does_not_run_when_the_deploy_failed()
    {
        _docker.UpExitCode = 1;

        Deployed(_deployment, Sha4);
        Deployed(_deployment, Sha3);
        Deployed(_deployment, Sha2);

        _docker.ImageTags["acme/site-web"] = [_git.NextSha, Sha2, Sha3, Sha4];

        await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        // The previous image is what the running containers came from.
        Assert.Empty(_docker.ImagesRemoved);
    }

    private async Task<string> FailureAsync()
    {
        var result = await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);
        var completed = Assert.IsType<DeployResult.Completed>(result);

        Assert.False(completed.Succeeded);

        return _deploys.Get(completed.LastDeployId)!.Error!;
    }

    [Fact]
    public async Task Token_mint_failure_is_recorded_with_its_prefix()
    {
        _minter.Fail = true;
        var result = await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        var completed = Assert.IsType<DeployResult.Completed>(result);
        Assert.False(completed.Succeeded);
        Assert.Contains("fetch:", _deploys.Get(completed.LastDeployId)!.Error);
    }

    [Fact]
    public async Task Drain_loops_exactly_once_for_a_single_mid_deploy_push()
    {
        // A push lands during pass 1 only.
        _git.OnFetch = call =>
        {
            if (call == 1) _deployments.SetDeployPending(_deployment.Id, true);
            return Task.CompletedTask;
        };

        var result = await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        var completed = Assert.IsType<DeployResult.Completed>(result);
        Assert.Equal(2, completed.Passes);

        var rows = Rows();
        Assert.Equal(2, rows.Count);
        Assert.Equal(DeployTrigger.Manual, rows[0].Trigger);
        Assert.Equal(DeployTrigger.Webhook, rows[1].Trigger);
        Assert.False(Current().DeployPending);
    }

    [Fact]
    public async Task Three_pass_bound_holds_under_a_push_flood_and_leaves_pending_set()
    {
        // Every pass sees a fresh push.
        _git.OnFetch = _ =>
        {
            _deployments.SetDeployPending(_deployment.Id, true);
            return Task.CompletedTask;
        };

        var result = await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        var completed = Assert.IsType<DeployResult.Completed>(result);
        Assert.Equal(Deployer.MaxPasses, completed.Passes);
        Assert.Equal(Deployer.MaxPasses, Rows().Count);
        Assert.True(Current().DeployPending);
        Assert.False(_locks.IsHeld(Deployer.LockKey(Repo, Branch)));
    }

    [Fact]
    public async Task Drain_runs_even_after_a_failed_deploy()
    {
        // A newer commit is often the fix for a broken one.
        _docker.UpExitCode = 1;
        _git.OnFetch = call =>
        {
            if (call == 1) _deployments.SetDeployPending(_deployment.Id, true);
            return Task.CompletedTask;
        };

        var result = await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Manual);

        var completed = Assert.IsType<DeployResult.Completed>(result);
        Assert.Equal(2, completed.Passes);
        Assert.False(Current().DeployPending);
    }

    [Fact]
    public async Task Deploy_on_an_inactive_deployment_runs_normally()
    {
        // Activation filtering guards only the webhook entry path.
        Assert.False(Current().WebhookActive);
        var result = await NewDeployer().DeployAsync(Repo, Branch, DeployTrigger.Webhook);
        Assert.IsType<DeployResult.Completed>(result);
    }

    [Fact]
    public async Task Tearing_down_stops_the_containers_and_frees_the_name()
    {
        await NewDeployer().TearDownAsync(_deployment);

        Assert.Contains("acme-site-main", _docker.DownedProjects);
        Assert.Null(_deployments.GetByBranch(Repo, Branch));
        Assert.True(_caddy.SyncCalls > 0);

        // The checkout stays, so a push brings the deployment back.
        Assert.True(Directory.Exists(_dirs.Paths.CheckoutDir(Repo, _deployment.BranchSlug)));
    }

    [Fact]
    public async Task Tearing_down_leaves_the_images_for_the_operator_to_prune()
    {
        Deployed(_deployment, Sha3);
        Deployed(_deployment, Sha2);

        _docker.ImageTags["acme/site-web"] = [Sha2, Sha3];

        await NewDeployer().TearDownAsync(_deployment);

        Assert.Empty(_docker.ImagesRemoved);
    }

    [Fact]
    public async Task Tearing_down_a_deployment_with_a_held_lock_does_nothing()
    {
        _locks.TryEnter(Deployer.LockKey(Repo, Branch));

        await NewDeployer().TearDownAsync(_deployment);

        Assert.Empty(_docker.DownedProjects);
        Assert.NotNull(_deployments.GetByBranch(Repo, Branch));
        Assert.True(_locks.IsHeld(Deployer.LockKey(Repo, Branch)));
    }
}
