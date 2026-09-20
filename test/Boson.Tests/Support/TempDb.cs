using Boson.Storage;
using Microsoft.Data.Sqlite;

namespace Boson.Tests.Support;

public sealed class TempDb : IDisposable
{
    public string DbPath { get; }
    public Db Db { get; }

    public TempDb(bool migrate = true)
    {
        DbPath = Path.Combine(Path.GetTempPath(), $"boson-test-{Guid.NewGuid():N}.db");

        // Unpooled, because the alternative is ClearAllPools() on teardown,
        // which reaches into connections other test classes are using in
        // parallel and disposes them mid-query.
        Db = new Db(DbPath, pooled: false);

        if (migrate) new Migrator(Db).MigrateToLatest();
    }

    public void Dispose()
    {
        foreach (var f in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" })
        {
            try { File.Delete(f); } catch { }
        }
    }
}
