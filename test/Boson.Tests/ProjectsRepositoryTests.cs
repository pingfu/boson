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
        Assert.Equal("site.example.com", p.Hostname);
        Assert.Equal(8080, p.UpstreamPort);
        Assert.Equal("main", p.Branch);
        Assert.Equal(1234, p.GithubAppId);
        Assert.Equal("boson-site", p.GithubAppSlug);
        Assert.Equal(5678, p.GithubInstallationId);
        Assert.Equal("s3cret", p.GithubWebhookSecret);
        Assert.False(p.WebhookActive);
        Assert.False(p.DeployPending);
        Assert.Null(p.ArchivedAt);
    }

    [Theory]
    [InlineData("acme/site", "other.example.com", 9090)]  // repo collision
    [InlineData("acme/other", "site.example.com", 9090)]  // hostname collision
    [InlineData("acme/other", "other.example.com", 8080)] // port collision
    public void Insert_throws_on_active_collision(string repo, string hostname, int port)
    {
        using var db = new TempDb();
        var repos = new ProjectsRepository(db.Db);
        repos.Insert(TestProjects.New());

        Assert.Throws<ProjectCollisionException>(() =>
            repos.Insert(TestProjects.New(repo: repo, hostname: hostname, port: port)));
    }

    [Fact]
    public void Archive_releases_repo_hostname_and_port_for_re_add()
    {
        using var db = new TempDb();
        var repos = new ProjectsRepository(db.Db);
        repos.Insert(TestProjects.New());
        repos.Archive("acme/site");

        Assert.Null(repos.GetByRepo("acme/site"));
        Assert.Empty(repos.ListActive());

        repos.Insert(TestProjects.New()); // same repo, hostname and port
        Assert.NotNull(repos.GetByRepo("acme/site"));
    }

    [Fact]
    public void Purge_cascades_deploy_history()
    {
        using var db = new TempDb();
        var repos = new ProjectsRepository(db.Db);
        var deploys = new DeploysRepository(db.Db);
        repos.Insert(TestProjects.New());
        var projectId = repos.GetByRepo("acme/site")!.Id;
        var deployId = deploys.Insert(projectId, DeployTrigger.Manual, id => $"/tmp/{id}.log");

        repos.Purge("acme/site");

        Assert.Null(repos.GetByRepo("acme/site"));
        Assert.Null(deploys.Get(deployId));
    }

    [Fact]
    public void Flags_flip_and_only_on_active_rows()
    {
        using var db = new TempDb();
        var repos = new ProjectsRepository(db.Db);
        repos.Insert(TestProjects.New());

        repos.MarkWebhookActive("acme/site");
        repos.SetDeployPending("acme/site", true);
        var p = repos.GetByRepo("acme/site")!;
        Assert.True(p.WebhookActive);
        Assert.True(p.DeployPending);

        repos.SetDeployPending("acme/site", false);
        Assert.False(repos.GetByRepo("acme/site")!.DeployPending);
    }

    [Fact]
    public void ListActive_excludes_archived()
    {
        using var db = new TempDb();
        var repos = new ProjectsRepository(db.Db);
        repos.Insert(TestProjects.New(repo: "acme/one", hostname: "one.example.com", port: 8001));
        repos.Insert(TestProjects.New(repo: "acme/two", hostname: "two.example.com", port: 8002));
        repos.Archive("acme/one");

        var active = repos.ListActive();
        Assert.Single(active);
        Assert.Equal("acme/two", active[0].Repo);
    }
}
