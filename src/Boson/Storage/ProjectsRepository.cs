using Dapper;
using Microsoft.Data.Sqlite;

namespace Boson.Storage;

public interface IProjectsRepository
{
    Project? GetByRepo(string repo);
    IReadOnlyList<Project> ListActive();
    /// <summary>Complete row only; throws when the repo is already added.</summary>
    long Insert(Project p);
    void Archive(string repo);
    void Purge(string repo);
}

public sealed class ProjectsRepository(Db db) : IProjectsRepository
{
    private const string Columns = """
        id AS Id,
        repo AS Repo,
        github_app_id AS GithubAppId,
        github_app_slug AS GithubAppSlug,
        github_installation_id AS GithubInstallationId,
        github_webhook_secret AS GithubWebhookSecret,
        github_app_pem AS GithubAppPem,
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

    public long Insert(Project p)
    {
        using var conn = db.Open();
        try
        {
            return conn.ExecuteScalar<long>("""
                INSERT INTO projects
                  (repo, github_app_id, github_app_slug, github_installation_id,
                   github_webhook_secret, github_app_pem)
                VALUES
                  (@Repo, @GithubAppId, @GithubAppSlug, @GithubInstallationId,
                   @GithubWebhookSecret, @GithubAppPem);
                SELECT last_insert_rowid();
                """, p);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            throw new ProjectCollisionException($"repo is already added: {p.Repo}");
        }
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
