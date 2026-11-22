using Boson.Storage;
using Boson.Tests.Support;
using Dapper;
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

    private static (string Secret, string Pem) StoredCredentials(TempDb db, string repo)
    {
        using var conn = db.Db.Open();

        return conn.QuerySingle<(string, string)>(
            "SELECT github_webhook_secret, github_app_pem FROM projects WHERE repo = @repo", new { repo });
    }

    [Fact]
    public void Archive_blanks_the_credentials_and_keeps_the_row()
    {
        // A re-add creates a new App, so nothing reads these again, and left
        // in place they are a working key in every backup.
        using var db = new TempDb();
        var repos = new ProjectsRepository(db.Db);
        repos.Insert(TestProjects.New());
        repos.Archive("acme/site");

        Assert.Equal(("", ""), StoredCredentials(db, "acme/site"));
    }

    [Fact]
    public void Credentials_archived_before_archiving_blanked_them_are_cleared()
    {
        using var db = new TempDb();
        var repos = new ProjectsRepository(db.Db);
        repos.Insert(TestProjects.New(repo: "acme/gone"));
        repos.Insert(TestProjects.New(repo: "acme/live"));

        using (var conn = db.Db.Open())
            conn.Execute("UPDATE projects SET archived_at = datetime('now') WHERE repo = 'acme/gone'");

        Assert.Equal(1, repos.ClearArchivedCredentials());
        Assert.Equal(("", ""), StoredCredentials(db, "acme/gone"));
        Assert.Equal("s3cret", StoredCredentials(db, "acme/live").Secret);

        // Nothing left to do the second time.
        Assert.Equal(0, repos.ClearArchivedCredentials());
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
