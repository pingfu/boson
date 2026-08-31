using Boson.Storage;
using Boson.Tests.Support;
using Xunit;

namespace Boson.Tests;

public class DeploysRepositoryTests
{
    private static long InsertProject(TempDb db)
    {
        var repos = new ProjectsRepository(db.Db);
        repos.Insert(TestProjects.New());
        return repos.GetByRepo("acme/site")!.Id;
    }

    [Fact]
    public void Insert_derives_log_path_from_row_id()
    {
        using var db = new TempDb();
        var projectId = InsertProject(db);
        var deploys = new DeploysRepository(db.Db);

        var id = deploys.Insert(projectId, DeployTrigger.Webhook, i => $"/var/log/boson/deploys/{i}.log");
        var row = deploys.Get(id)!;

        Assert.Equal($"/var/log/boson/deploys/{id}.log", row.LogPath);
        Assert.Equal("webhook", row.Trigger);
        Assert.Equal("running", row.Status);
        Assert.Null(row.FinishedAt);
    }

    [Fact]
    public void Finish_records_outcome_and_timestamps()
    {
        using var db = new TempDb();
        var projectId = InsertProject(db);
        var deploys = new DeploysRepository(db.Db);
        var id = deploys.Insert(projectId, DeployTrigger.Manual, i => $"{i}.log");

        deploys.SetCommitSha(id, "abc123");
        deploys.Finish(id, succeeded: false, error: "docker compose up exited 1");

        var row = deploys.Get(id)!;
        Assert.Equal("failed", row.Status);
        Assert.Equal("abc123", row.CommitSha);
        Assert.Equal("docker compose up exited 1", row.Error);
        Assert.NotNull(row.FinishedAt);
    }

    [Fact]
    public void Latest_returns_newest_row()
    {
        using var db = new TempDb();
        var projectId = InsertProject(db);
        var deploys = new DeploysRepository(db.Db);
        deploys.Insert(projectId, DeployTrigger.Manual, i => $"{i}.log");
        var second = deploys.Insert(projectId, DeployTrigger.Webhook, i => $"{i}.log");

        Assert.Equal(second, deploys.GetLatestForProject(projectId)!.Id);
        Assert.Equal(2, deploys.ListForProject(projectId).Count);
    }

    [Fact]
    public void Startup_recovery_fails_only_running_rows()
    {
        using var db = new TempDb();
        var projectId = InsertProject(db);
        var deploys = new DeploysRepository(db.Db);
        var finished = deploys.Insert(projectId, DeployTrigger.Manual, i => $"{i}.log");
        deploys.Finish(finished, succeeded: true, error: null);
        var orphan = deploys.Insert(projectId, DeployTrigger.Webhook, i => $"{i}.log");

        var recovered = deploys.MarkAllRunningAsFailed("daemon restart");

        Assert.Equal(1, recovered);
        Assert.Equal("succeeded", deploys.Get(finished)!.Status);
        var orphanRow = deploys.Get(orphan)!;
        Assert.Equal("failed", orphanRow.Status);
        Assert.Equal("daemon restart", orphanRow.Error);
    }
}
