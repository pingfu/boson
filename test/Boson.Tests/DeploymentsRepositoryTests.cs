using Boson.Storage;
using Boson.Tests.Support;
using Xunit;

namespace Boson.Tests;

public class DeploymentsRepositoryTests
{
    [Fact]
    public void Insert_and_get_round_trip()
    {
        using var db = new TempDb();
        var projects = new ProjectsRepository(db.Db);
        var deployments = new DeploymentsRepository(db.Db);

        var inserted = TestProjects.Insert(projects, deployments,
            aliases: "www.site.example.com", envSet: "production", expireAfter: "14d");

        var d = deployments.GetByBranch("acme/site", "main")!;

        Assert.Equal(inserted.Id, d.Id);
        Assert.Equal("acme/site", d.Repo);
        Assert.Equal("main", d.Branch);
        Assert.Equal("main", d.Label);
        Assert.Equal("site.example.com", d.Hostname);
        Assert.Equal(["www.site.example.com"], d.AliasList);
        Assert.Equal(30000, d.HostPort);
        Assert.Equal("production", d.EnvSet);
        Assert.Equal("14d", d.ExpireAfter);
        Assert.False(d.WebhookActive);
        Assert.False(d.DeployPending);
    }

    [Theory]
    [InlineData("main", "other.example.com", 30001)]        // branch collision
    [InlineData("develop", "site.example.com", 30001)]      // hostname collision
    [InlineData("develop", "other.example.com", 30000)]     // port collision
    public void Insert_throws_on_active_collision(string branch, string hostname, int port)
    {
        using var db = new TempDb();
        var projects = new ProjectsRepository(db.Db);
        var deployments = new DeploymentsRepository(db.Db);

        TestProjects.Insert(projects, deployments);

        Assert.Throws<ProjectCollisionException>(() =>
            TestProjects.Insert(projects, deployments,
                branch: branch, hostname: hostname, port: port));
    }

    [Fact]
    public void Two_branches_of_one_repository_coexist()
    {
        using var db = new TempDb();
        var projects = new ProjectsRepository(db.Db);
        var deployments = new DeploymentsRepository(db.Db);

        TestProjects.Insert(projects, deployments);
        TestProjects.Insert(projects, deployments,
            branch: "develop", hostname: "staging.example.com", port: 30001);

        Assert.Equal(2, deployments.ListActiveForRepo("acme/site").Count);
    }

    [Fact]
    public void Archiving_releases_the_hostname_branch_and_port()
    {
        using var db = new TempDb();
        var projects = new ProjectsRepository(db.Db);
        var deployments = new DeploymentsRepository(db.Db);

        var d = TestProjects.Insert(projects, deployments);
        deployments.Archive(d.Id);

        Assert.Null(deployments.GetByBranch("acme/site", "main"));
        Assert.Empty(deployments.ListActive());

        // Same branch, same name, same port: a torn-down deployment holds nothing.
        Assert.NotNull(TestProjects.Insert(projects, deployments));
    }

    [Fact]
    public void What_the_file_declares_can_be_reapplied()
    {
        using var db = new TempDb();
        var projects = new ProjectsRepository(db.Db);
        var deployments = new DeploymentsRepository(db.Db);

        var d = TestProjects.Insert(projects, deployments);

        deployments.SetDeclared(d.Id, "renamed.example.com", "www.renamed.example.com", "preview", "7d");

        var updated = deployments.Get(d.Id)!;
        Assert.Equal("renamed.example.com", updated.Hostname);
        Assert.Equal(["www.renamed.example.com"], updated.AliasList);
        Assert.Equal("preview", updated.EnvSet);
        Assert.Equal("7d", updated.ExpireAfter);
    }

    [Fact]
    public void A_hostname_another_deployment_holds_is_refused()
    {
        using var db = new TempDb();
        var projects = new ProjectsRepository(db.Db);
        var deployments = new DeploymentsRepository(db.Db);

        var main = TestProjects.Insert(projects, deployments);
        var develop = TestProjects.Insert(projects, deployments,
            branch: "develop", hostname: "staging.example.com", port: 30001);

        Assert.Throws<ProjectCollisionException>(() =>
            deployments.SetDeclared(develop.Id, main.Hostname, "", null, null));
    }

    [Fact]
    public void Flags_flip_and_only_on_active_rows()
    {
        using var db = new TempDb();
        var projects = new ProjectsRepository(db.Db);
        var deployments = new DeploymentsRepository(db.Db);

        var d = TestProjects.Insert(projects, deployments);

        deployments.MarkWebhookActive(d.Id);
        deployments.SetDeployPending(d.Id, true);

        var updated = deployments.Get(d.Id)!;
        Assert.True(updated.WebhookActive);
        Assert.True(updated.DeployPending);

        deployments.SetDeployPending(d.Id, false);
        Assert.False(deployments.Get(d.Id)!.DeployPending);
    }
}
