using Dapper;

namespace Boson.Storage;

public interface IDeploysRepository
{
    /// <summary>
    /// The log path is derived from the row id, so the row is
    /// inserted first and the path filled in inside the same transaction.
    /// </summary>
    long Insert(long deploymentId, DeployTrigger trigger, Func<long, string> logPathFor);
    void SetCommitSha(long deployId, string sha);
    void Finish(long deployId, bool succeeded, string? error);
    DeployRow? Get(long deployId);
    DeployRow? GetLatestForDeployment(long deploymentId);
    DeployRow? GetLastSucceededForDeployment(long deploymentId);
    IReadOnlyList<DeployRow> ListForDeployment(long deploymentId);
    /// <summary>
    /// Startup recovery: a 'running' row without a live daemon task
    /// can only be a crash orphan, because the daemon is the sole deploy executor.
    /// </summary>
    int MarkAllRunningAsFailed(string error);
}

public sealed class DeploysRepository(Db db) : IDeploysRepository
{
    private const string Columns = """
        id AS Id,
        deployment_id AS DeploymentId,
        "trigger" AS Trigger,
        commit_sha AS CommitSha,
        started_at AS StartedAt,
        finished_at AS FinishedAt,
        status AS Status,
        log_path AS LogPath,
        error AS Error
        """;

    public long Insert(long deploymentId, DeployTrigger trigger, Func<long, string> logPathFor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        var id = conn.ExecuteScalar<long>("""
            INSERT INTO deploys(deployment_id, "trigger", log_path)
            VALUES (@deploymentId, @trigger, '');
            SELECT last_insert_rowid();
            """, new { deploymentId, trigger = trigger.AsDbValue() }, tx);

        conn.Execute("UPDATE deploys SET log_path = @logPath WHERE id = @id",
            new { logPath = logPathFor(id), id }, tx);

        tx.Commit();

        return id;
    }

    public void SetCommitSha(long deployId, string sha)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE deploys SET commit_sha = @sha WHERE id = @deployId",
            new { deployId, sha });
    }

    public void Finish(long deployId, bool succeeded, string? error)
    {
        using var conn = db.Open();
        conn.Execute("""
            UPDATE deploys
            SET status = @status, finished_at = datetime('now'), error = @error
            WHERE id = @deployId
            """, new
            {
                deployId,
                status = (succeeded ? DeployStatus.Succeeded : DeployStatus.Failed).AsDbValue(),
                error,
            });
    }

    public DeployRow? Get(long deployId)
    {
        using var conn = db.Open();

        return conn.QuerySingleOrDefault<DeployRow>(
            $"SELECT {Columns} FROM deploys WHERE id = @deployId", new { deployId });
    }

    public DeployRow? GetLatestForDeployment(long deploymentId)
    {
        using var conn = db.Open();

        return conn.QueryFirstOrDefault<DeployRow>(
            $"""
            SELECT {Columns} FROM deploys WHERE deployment_id = @deploymentId
            ORDER BY id DESC LIMIT 1
            """, new { deploymentId });
    }

    /// <summary>What expiry measures from: a deployment is idle since its last working deploy.</summary>
    public DeployRow? GetLastSucceededForDeployment(long deploymentId)
    {
        using var conn = db.Open();

        return conn.QueryFirstOrDefault<DeployRow>(
            $"""
            SELECT {Columns} FROM deploys
            WHERE deployment_id = @deploymentId AND status = 'succeeded'
            ORDER BY id DESC LIMIT 1
            """, new { deploymentId });
    }

    public IReadOnlyList<DeployRow> ListForDeployment(long deploymentId)
    {
        using var conn = db.Open();

        return conn.Query<DeployRow>(
            $"SELECT {Columns} FROM deploys WHERE deployment_id = @deploymentId ORDER BY id",
            new { deploymentId }).ToList();
    }

    public int MarkAllRunningAsFailed(string error)
    {
        using var conn = db.Open();

        return conn.Execute("""
            UPDATE deploys
            SET status = 'failed', finished_at = datetime('now'), error = @error
            WHERE status = 'running'
            """, new { error });
    }
}
