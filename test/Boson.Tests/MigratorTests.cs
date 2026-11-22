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
    public void A_project_saved_before_the_app_org_column_is_a_personal_accounts_app()
    {
        using var db = new TempDb(migrate: false);

        // The database as the first migration alone left it, holding a project.
        var asm = typeof(Migrator).Assembly;
        var init = asm.GetManifestResourceNames().Single(n => n.EndsWith("0001_init.sql"));

        using (var reader = new StreamReader(asm.GetManifestResourceStream(init)!))
        using (var conn = db.Db.Open())
        {
            conn.Execute(reader.ReadToEnd());
            conn.Execute("INSERT INTO schema_version(version) VALUES (1)");
            conn.Execute("""
                INSERT INTO projects
                  (repo, github_app_id, github_app_slug, github_installation_id,
                   github_webhook_secret, github_app_pem)
                VALUES ('acme/site', 1234, 'boson-site', 5678, 's3cret', 'pem')
                """);
        }

        new Migrator(db.Db).MigrateToLatest();

        var project = new ProjectsRepository(db.Db).GetByRepo("acme/site")!;

        Assert.Null(project.GithubAppOrg);
        Assert.Equal("pem", project.GithubAppPem);
        Assert.Equal("https://github.com/settings/apps/boson-site", project.AppSettingsUrl);
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
