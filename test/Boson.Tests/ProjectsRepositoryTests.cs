using Boson.Storage;
using Boson.Tests.Support;
using Xunit;

namespace Boson.Tests;

public class ProjectsRepositoryTests
{
    [Fact]
    public void Insert_and_get_round_trip()
    {
        using var db = new TempDb();
        var repos = new ProjectsRepository(db.Db);
        repos.Insert(TestProjects.New());

        var p = repos.GetByRepo("acme/site");
        Assert.NotNull(p);
        Assert.Equal(1234, p.GithubAppId);
        Assert.Equal("boson-site", p.GithubAppSlug);
        Assert.Equal(5678, p.GithubInstallationId);
        Assert.Equal("s3cret", p.GithubWebhookSecret);
        Assert.Null(p.ArchivedAt);
    }

    [Fact]
    public void Insert_throws_when_the_repo_is_already_added()
    {
        using var db = new TempDb();
        var repos = new ProjectsRepository(db.Db);
        repos.Insert(TestProjects.New());

        Assert.Throws<ProjectCollisionException>(() => repos.Insert(TestProjects.New()));
    }

    [Fact]
    public void Archive_releases_the_repo_for_re_add()
    {
        using var db = new TempDb();
        var repos = new ProjectsRepository(db.Db);
        repos.Insert(TestProjects.New());
        repos.Archive("acme/site");

        Assert.Null(repos.GetByRepo("acme/site"));
        Assert.Empty(repos.ListActive());

        repos.Insert(TestProjects.New());
        Assert.NotNull(repos.GetByRepo("acme/site"));
    }

    [Fact]
    public void Purge_cascades_deployments_and_their_history()
    {
        using var db = new TempDb();
        var repos = new ProjectsRepository(db.Db);
        var deployments = new DeploymentsRepository(db.Db);
        var deploys = new DeploysRepository(db.Db);

        var deployment = TestProjects.Insert(repos, deployments);
        var deployId = deploys.Insert(deployment.Id, DeployTrigger.Manual, id => $"/tmp/{id}.log");

        repos.Purge("acme/site");

        Assert.Null(repos.GetByRepo("acme/site"));
        Assert.Null(deployments.Get(deployment.Id));
        Assert.Null(deploys.Get(deployId));
    }

    [Fact]
    public void ListActive_excludes_archived()
    {
        using var db = new TempDb();
        var repos = new ProjectsRepository(db.Db);
        repos.Insert(TestProjects.New(repo: "acme/one"));
        repos.Insert(TestProjects.New(repo: "acme/two"));
        repos.Archive("acme/one");

        var active = repos.ListActive();
        Assert.Single(active);
        Assert.Equal("acme/two", active[0].Repo);
    }
}
