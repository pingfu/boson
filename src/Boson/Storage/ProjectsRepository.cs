using Dapper;
using Microsoft.Data.Sqlite;

namespace Boson.Storage;

public interface IProjectsRepository
{
    Project? GetByRepo(string repo);
    IReadOnlyList<Project> ListActive();
    /// <summary>Complete row only (spec §11); throws on repo/hostname/port collision.</summary>
    void Insert(Project p);
    void MarkWebhookActive(string repo);
    void SetDeployPending(string repo, bool value);
    void Archive(string repo);
    void Purge(string repo);
}

public sealed class ProjectsRepository(Db db) : IProjectsRepository
{
    private const string Columns = """
        id AS Id, 
        repo AS Repo, 
        hostname AS Hostname, 
        upstream_port AS UpstreamPort,
        branch AS Branch, 
        github_app_id AS GithubAppId, 
        github_app_slug AS GithubAppSlug,
        github_installation_id AS GithubInstallationId,
        github_webhook_secret AS GithubWebhookSecret, 
        github_app_pem AS GithubAppPem,
        webhook_active AS WebhookActive, 
        deploy_pending AS DeployPending,
        created_at AS CreatedAt, 
        updated_at AS UpdatedAt, 
        archived_at AS ArchivedAt
        """;

    public Project? GetByRepo(string repo)
    {
        using var conn = db.Open();
        return conn.QuerySingleOrDefault<Project>(
            $"SELECT {Columns} FROM projects WHERE repo = @repo AND archived_at IS NULL",
            new { repo });
    }

    public IReadOnlyList<Project> ListActive()
    {
        using var conn = db.Open();
        return conn.Query<Project>(
            $"SELECT {Columns} FROM projects WHERE archived_at IS NULL ORDER BY repo")
            .ToList();
    }

    public void Insert(Project p)
    {
        using var conn = db.Open();
        try
        {
            conn.Execute("""
                INSERT INTO projects
                  (repo, hostname, upstream_port, branch,
                   github_app_id, github_app_slug, github_installation_id,
                   github_webhook_secret, github_app_pem)
                VALUES
                  (@Repo, @Hostname, @UpstreamPort, @Branch,
                   @GithubAppId, @GithubAppSlug, @GithubInstallationId,
                   @GithubWebhookSecret, @GithubAppPem)
                """, p);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            throw new ProjectCollisionException(
                "repo, hostname or upstream port is already claimed by an active project");
        }
    }

    public void MarkWebhookActive(string repo) => SetFlag(repo, "webhook_active", true);

    public void SetDeployPending(string repo, bool value) => SetFlag(repo, "deploy_pending", value);

    private void SetFlag(string repo, string column, bool value)
    {
        using var conn = db.Open();
        conn.Execute(
            $"""
            UPDATE projects SET {column} = @value, updated_at = datetime('now')
            WHERE repo = @repo AND archived_at IS NULL
            """,
            new { repo, value = value ? 1 : 0 });
    }

    public void Archive(string repo)
    {
        using var conn = db.Open();
        conn.Execute("""
            UPDATE projects SET archived_at = datetime('now'), updated_at = datetime('now')
            WHERE repo = @repo AND archived_at IS NULL
            """, new { repo });
    }

    public void Purge(string repo)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM projects WHERE repo = @repo", new { repo });
    }
}

public sealed class ProjectCollisionException(string message) : Exception(message);
