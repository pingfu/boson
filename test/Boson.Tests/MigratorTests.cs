using Boson.Storage;
using Boson.Tests.Support;
using Dapper;
using Xunit;

namespace Boson.Tests;

public class MigratorTests
{
    [Fact]
    public void Fresh_database_migrates_to_binary_version()
    {
        using var db = new TempDb(migrate: false);
        var migrator = new Migrator(db.Db);
        migrator.MigrateToLatest();

        Assert.Equal(Migrator.BinarySchemaVersion, migrator.CurrentVersion());
        using var conn = db.Db.Open();
        foreach (var table in new[] { "schema_version", "platform", "projects", "deploys" })
            Assert.Equal(1, conn.ExecuteScalar<long>(
                "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=@table",
                new { table }));
    }

    [Fact]
    public void Migrate_is_idempotent()
    {
        using var db = new TempDb();
        var migrator = new Migrator(db.Db);
        migrator.MigrateToLatest();
        Assert.Equal(Migrator.BinarySchemaVersion, migrator.CurrentVersion());
    }

    [Fact]
    public void Compatible_database_passes_the_gate()
    {
        using var db = new TempDb();
        new Migrator(db.Db).CheckCompatibility();
    }

    [Fact]
    public void Database_behind_binary_points_at_daemon_restart()
    {
        using var db = new TempDb(migrate: false);
        var e = Assert.Throws<SchemaMismatchException>(() => new Migrator(db.Db).CheckCompatibility());
        Assert.Contains("systemctl restart boson", e.Message);
    }

    [Fact]
    public void Database_ahead_of_binary_points_at_binary_upgrade()
    {
        using var db = new TempDb();
        using (var conn = db.Db.Open())
            conn.Execute("INSERT INTO schema_version(version) VALUES (9999)");

        var e = Assert.Throws<SchemaMismatchException>(() => new Migrator(db.Db).CheckCompatibility());
        Assert.Contains("upgrade the binary", e.Message);
    }

    [Fact]
    public void Daemon_refuses_to_migrate_a_database_ahead_of_its_binary()
    {
        using var db = new TempDb();
        using (var conn = db.Db.Open())
            conn.Execute("INSERT INTO schema_version(version) VALUES (9999)");

        var e = Assert.Throws<SchemaMismatchException>(() => new Migrator(db.Db).MigrateToLatest());
        Assert.Contains("upgrade the binary", e.Message);
    }
}
