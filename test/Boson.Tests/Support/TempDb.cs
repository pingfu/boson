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
        Db = new Db(DbPath);
        if (migrate) new Migrator(Db).MigrateToLatest();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" })
        {
            try { File.Delete(f); } catch { }
        }
    }
}
