using Dapper;
using Microsoft.Data.Sqlite;

namespace Boson.Storage;

public interface IDeploymentsRepository
{
    Deployment? Get(long id);
    Deployment? GetByBranch(string repo, string branch);
    IReadOnlyList<Deployment> ListActive();
    IReadOnlyList<Deployment> ListActiveForRepo(string repo);

    /// <summary>Throws on a hostname, port, branch or label another active deployment holds.</summary>
    long Insert(Deployment deployment);

    /// <summary>What `_boson.yml` decides, reapplied on every deploy.</summary>
    void SetDeclared(long id, string hostname, string aliases, string? envSet, string? expireAfter);

    void MarkWebhookActive(long id);
    void SetDeployPending(long id, bool value);
    void Archive(long id);
}

public sealed class DeploymentsRepository(Db db) : IDeploymentsRepository
{
    private const string Columns = """
        d.id AS Id,
        d.project_id AS ProjectId,
        p.repo AS Repo,
        d.branch AS Branch,
        d.label AS Label,
        d.hostname AS Hostname,
        d.aliases AS Aliases,
        d.host_port AS HostPort,
        d.env_set AS EnvSet,
        d.expire_after AS ExpireAfter,
        d.webhook_active AS WebhookActive,
        d.deploy_pending AS DeployPending,
        d.created_at AS CreatedAt,
        d.updated_at AS UpdatedAt,
        d.archived_at AS ArchivedAt
        """;

    private const string From = """
        FROM deployments d
        JOIN projects p ON p.id = d.project_id
        """;

    public Deployment? Get(long id)
    {
        using var conn = db.Open();

        return conn.QuerySingleOrDefault<Deployment>(
            $"SELECT {Columns} {From} WHERE d.id = @id", new { id });
    }

    public Deployment? GetByBranch(string repo, string branch)
    {
        using var conn = db.Open();

        return conn.QuerySingleOrDefault<Deployment>(
            $"""
            SELECT {Columns} {From}
            WHERE p.repo = @repo AND d.branch = @branch
              AND d.archived_at IS NULL AND p.archived_at IS NULL
            """, new { repo, branch });
    }

    public IReadOnlyList<Deployment> ListActive()
    {
        using var conn = db.Open();

        return conn.Query<Deployment>(
            $"""
            SELECT {Columns} {From}
            WHERE d.archived_at IS NULL AND p.archived_at IS NULL
            ORDER BY p.repo, d.branch
            """).ToList();
    }

    public IReadOnlyList<Deployment> ListActiveForRepo(string repo)
    {
        using var conn = db.Open();

        return conn.Query<Deployment>(
            $"""
            SELECT {Columns} {From}
            WHERE p.repo = @repo AND d.archived_at IS NULL AND p.archived_at IS NULL
            ORDER BY d.branch
            """, new { repo }).ToList();
    }

    public long Insert(Deployment deployment)
    {
        using var conn = db.Open();

        try
        {
            return conn.ExecuteScalar<long>("""
                INSERT INTO deployments
                  (project_id, branch, label, hostname, aliases, host_port, env_set, expire_after)
                VALUES
                  (@ProjectId, @Branch, @Label, @Hostname, @Aliases, @HostPort, @EnvSet, @ExpireAfter);
                SELECT last_insert_rowid();
                """, deployment);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            throw new ProjectCollisionException(
                $"{deployment.Hostname}, host port {deployment.HostPort} or branch " +
                $"{deployment.Branch} is already claimed by an active deployment");
        }
    }

    public void SetDeclared(long id, string hostname, string aliases, string? envSet, string? expireAfter)
    {
        using var conn = db.Open();

        try
        {
            conn.Execute("""
                UPDATE deployments
                SET hostname = @hostname, aliases = @aliases, env_set = @envSet,
                    expire_after = @expireAfter, updated_at = datetime('now')
                WHERE id = @id
                """, new { id, hostname, aliases, envSet, expireAfter });
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            throw new ProjectCollisionException(
                $"{hostname} is already claimed by another active deployment");
        }
    }

    public void MarkWebhookActive(long id) => SetFlag(id, "webhook_active", true);

    public void SetDeployPending(long id, bool value) => SetFlag(id, "deploy_pending", value);

    private void SetFlag(long id, string column, bool value)
    {
        using var conn = db.Open();

        conn.Execute(
            $"""
            UPDATE deployments SET {column} = @value, updated_at = datetime('now')
            WHERE id = @id AND archived_at IS NULL
            """, new { id, value = value ? 1 : 0 });
    }

    public void Archive(long id)
    {
        using var conn = db.Open();

        conn.Execute("""
            UPDATE deployments SET archived_at = datetime('now'), updated_at = datetime('now')
            WHERE id = @id AND archived_at IS NULL
            """, new { id });
    }
}
