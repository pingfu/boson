using Dapper;
using Microsoft.Data.Sqlite;

namespace Boson.Storage;

public interface IDeploymentsRepository
{
    Deployment? Get(long id);
    Deployment? GetByBranch(string repo, string branch);
    IReadOnlyList<Deployment> ListActive();
    IReadOnlyList<Deployment> ListActiveForRepo(string repo);

    /// <summary>Throws on a hostname, alias, port, branch or label another active deployment holds.</summary>
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
        d.branch_slug AS BranchSlug,
        d.hostname AS Hostname,
        COALESCE((
            SELECT group_concat(name, ',')
            FROM (
                SELECT name FROM deployment_names
                WHERE deployment_id = d.id AND kind = 'alias'
                ORDER BY name
            )
        ), '') AS Aliases,
        d.port AS Port,
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
        using var tx = conn.BeginTransaction();

        try
        {
            var id = conn.ExecuteScalar<long>("""
                INSERT INTO deployments
                  (project_id, branch, branch_slug, hostname, port, env_set, expire_after)
                VALUES
                  (@ProjectId, @Branch, @BranchSlug, @Hostname, @Port, @EnvSet, @ExpireAfter);
                SELECT last_insert_rowid();
                """, deployment, tx);

            ReplaceNames(conn, tx, id, deployment.Hostname, deployment.AliasList);

            tx.Commit();

            return id;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            tx.Rollback();
            throw new ProjectCollisionException(
                $"{deployment.Hostname}, aliases, port {deployment.Port} or branch " +
                $"{deployment.Branch} is already claimed by an active deployment");
        }
    }

    public void SetDeclared(long id, string hostname, string aliases, string? envSet, string? expireAfter)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        try
        {
            conn.Execute("""
                UPDATE deployments
                SET hostname = @hostname, env_set = @envSet,
                    expire_after = @expireAfter, updated_at = datetime('now')
                WHERE id = @id
                """, new { id, hostname, envSet, expireAfter }, tx);

            ReplaceNames(conn, tx, id, hostname, SplitAliases(aliases));

            tx.Commit();
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            tx.Rollback();
            throw new ProjectCollisionException(
                $"{hostname} or one of its aliases is already claimed by another active deployment");
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
        using var tx = conn.BeginTransaction();

        conn.Execute("""
            UPDATE deployments SET archived_at = datetime('now'), updated_at = datetime('now')
            WHERE id = @id AND archived_at IS NULL
            """, new { id }, tx);

        conn.Execute("DELETE FROM deployment_names WHERE deployment_id = @id", new { id }, tx);

        tx.Commit();
    }

    private static void ReplaceNames(
        SqliteConnection conn,
        SqliteTransaction tx,
        long deploymentId,
        string hostname,
        IEnumerable<string> aliases)
    {
        conn.Execute("DELETE FROM deployment_names WHERE deployment_id = @deploymentId",
            new { deploymentId }, tx);

        conn.Execute("""
            INSERT INTO deployment_names(deployment_id, name, kind)
            VALUES (@deploymentId, @name, 'primary')
            """, new { deploymentId, name = hostname }, tx);

        foreach (var alias in aliases
                     .Select(a => a.Trim())
                     .Where(a => a.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            conn.Execute("""
                INSERT INTO deployment_names(deployment_id, name, kind)
                VALUES (@deploymentId, @name, 'alias')
                """, new { deploymentId, name = alias }, tx);
        }
    }

    private static IReadOnlyList<string> SplitAliases(string aliases) =>
        aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
