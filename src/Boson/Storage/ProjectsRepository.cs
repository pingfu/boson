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
        github_app_org AS GithubAppOrg,
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
                   github_webhook_secret, github_app_pem, github_app_org)
                VALUES
                  (@Repo, @GithubAppId, @GithubAppSlug, @GithubInstallationId,
                   @GithubWebhookSecret, @GithubAppPem, @GithubAppOrg);
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
        // The secret and the key go with it. A re-add creates a new App, so an
        // archived row's credentials are never read again, and keeping them
        // would leave a working key for a repository in every backup.
        conn.Execute("""
            UPDATE projects
            SET archived_at = datetime('now'), updated_at = datetime('now'),
                github_webhook_secret = '', github_app_pem = ''
            WHERE repo = @repo AND archived_at IS NULL
            """, new { repo });

        Scrub(conn);
    }

    /// <summary>
    /// Blanks the credentials of rows archived before archiving did so itself.
    /// Returns how many rows it changed.
    /// </summary>
    public int ClearArchivedCredentials()
    {
        using var conn = db.Open();

        var cleared = conn.Execute("""
            UPDATE projects
            SET github_webhook_secret = '', github_app_pem = '', updated_at = datetime('now')
            WHERE archived_at IS NOT NULL
              AND (github_webhook_secret <> '' OR github_app_pem <> '')
            """);

        if (cleared > 0) Scrub(conn);

        return cleared;
    }

    public void Purge(string repo)
    {
        using var conn = db.Open();
        conn.Execute("DELETE FROM projects WHERE repo = @repo", new { repo });

        Scrub(conn);
    }

    /// <summary>
    /// secure_delete zeroes the key in the database file, but the write-ahead
    /// log still carries the page as it was when the key was stored, until the
    /// log is folded back in and emptied. A reader holding the log open makes
    /// this a no-op, and the next checkpoint does it.
    /// </summary>
    private static void Scrub(Microsoft.Data.Sqlite.SqliteConnection conn) =>
        conn.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
}

public sealed class ProjectCollisionException(string message) : Exception(message);
