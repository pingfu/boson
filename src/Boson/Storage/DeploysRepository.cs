using Dapper;

namespace Boson.Storage;

public interface IDeploysRepository
{
    /// <summary>
    /// The log path is derived from the row id, so the row is
    /// inserted first and the path filled in inside the same transaction.
    /// </summary>
    long Insert(long projectId, DeployTrigger trigger, Func<long, string> logPathFor);
    void SetCommitSha(long deployId, string sha);
    void Finish(long deployId, bool succeeded, string? error);
    DeployRow? Get(long deployId);
    DeployRow? GetLatestForProject(long projectId);
    IReadOnlyList<DeployRow> ListForProject(long projectId);
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
        project_id AS ProjectId, 
        "trigger" AS Trigger, 
        commit_sha AS CommitSha,
        started_at AS StartedAt, 
        finished_at AS FinishedAt, 
        status AS Status,
        log_path AS LogPath, 
        error AS Error
        """;

    public long Insert(long projectId, DeployTrigger trigger, Func<long, string> logPathFor)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        var id = conn.ExecuteScalar<long>("""
            INSERT INTO deploys(project_id, "trigger", log_path)
            VALUES (@projectId, @trigger, '');
            SELECT last_insert_rowid();
            """, new { projectId, trigger = trigger.AsDbValue() }, tx);
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

    public DeployRow? GetLatestForProject(long projectId)
    {
        using var conn = db.Open();

        return conn.QueryFirstOrDefault<DeployRow>(
            $"""
            SELECT {Columns} FROM deploys WHERE project_id = @projectId
            ORDER BY id DESC LIMIT 1
            """, new { projectId });
    }

    public IReadOnlyList<DeployRow> ListForProject(long projectId)
    {
        using var conn = db.Open();

        return conn.Query<DeployRow>(
            $"SELECT {Columns} FROM deploys WHERE project_id = @projectId ORDER BY id",
            new { projectId }).ToList();
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
