using Boson.Storage;
using Boson.Tests.Support;
using Xunit;

namespace Boson.Tests;

public class DeploysRepositoryTests
{
    private static long InsertDeployment(TempDb db) =>
        TestProjects.Insert(new ProjectsRepository(db.Db), new DeploymentsRepository(db.Db)).Id;

    [Fact]
    public void Insert_derives_log_path_from_row_id()
    {
        using var db = new TempDb();
        var deploymentId = InsertDeployment(db);
        var deploys = new DeploysRepository(db.Db);

        var id = deploys.Insert(deploymentId, DeployTrigger.Webhook, i => $"/var/log/boson/deploys/{i}.log");
        var row = deploys.Get(id)!;

        Assert.Equal($"/var/log/boson/deploys/{id}.log", row.LogPath);
        Assert.Equal(DeployTrigger.Webhook, row.Trigger);
        Assert.Equal(DeployStatus.Running, row.Status);
        Assert.Null(row.FinishedAt);
    }

    [Fact]
    public void Finish_records_outcome_and_timestamps()
    {
        using var db = new TempDb();
        var deploymentId = InsertDeployment(db);
        var deploys = new DeploysRepository(db.Db);
        var id = deploys.Insert(deploymentId, DeployTrigger.Manual, i => $"{i}.log");

        deploys.SetCommitSha(id, "abc123");
        deploys.Finish(id, succeeded: false, error: "docker compose up exited 1");

        var row = deploys.Get(id)!;
        Assert.Equal(DeployStatus.Failed, row.Status);
        Assert.Equal("abc123", row.CommitSha);
        Assert.Equal("docker compose up exited 1", row.Error);
        Assert.NotNull(row.FinishedAt);
    }

    [Fact]
    public void Latest_returns_newest_row()
    {
        using var db = new TempDb();
        var deploymentId = InsertDeployment(db);
        var deploys = new DeploysRepository(db.Db);
        deploys.Insert(deploymentId, DeployTrigger.Manual, i => $"{i}.log");
        var second = deploys.Insert(deploymentId, DeployTrigger.Webhook, i => $"{i}.log");

        Assert.Equal(second, deploys.GetLatestForDeployment(deploymentId)!.Id);
        Assert.Equal(2, deploys.ListForDeployment(deploymentId).Count);
    }

    [Fact]
    public void Startup_recovery_fails_only_running_rows()
    {
        using var db = new TempDb();
        var deploymentId = InsertDeployment(db);
        var deploys = new DeploysRepository(db.Db);
        var finished = deploys.Insert(deploymentId, DeployTrigger.Manual, i => $"{i}.log");
        deploys.Finish(finished, succeeded: true, error: null);
        var orphan = deploys.Insert(deploymentId, DeployTrigger.Webhook, i => $"{i}.log");

        var recovered = deploys.MarkAllRunningAsFailed("daemon restart");

        Assert.Equal(1, recovered);
        Assert.Equal(DeployStatus.Succeeded, deploys.Get(finished)!.Status);
        var orphanRow = deploys.Get(orphan)!;
        Assert.Equal(DeployStatus.Failed, orphanRow.Status);
        Assert.Equal("daemon restart", orphanRow.Error);
    }
}
