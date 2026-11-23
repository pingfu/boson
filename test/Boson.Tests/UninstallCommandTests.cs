using Boson.Commands;
using Boson.Storage;
using Boson.Tests.Support;
using Dapper;
using Xunit;

namespace Boson.Tests;

public sealed class UninstallCommandTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly TempDb _db = new();
    private readonly TempDirs _dirs = new();

    public UninstallCommandTests() => new ProjectsRepository(_db.Db).Insert(TestProjects.New());

    public void Dispose()
    {
        _db.Dispose();
        _dirs.Dispose();
    }

    private string Target => Path.Combine(_dirs.Root, "final-backup.db");

    [Fact]
    public void The_backup_is_a_database_holding_the_projects()
    {
        UninstallCommand.WriteBackup(_db.Db, _dirs.Paths.TmpDir, Target);

        using var conn = new Db(Target, pooled: false).Open();

        Assert.Equal("acme/site", conn.QuerySingle<string>("SELECT repo FROM projects"));
    }

    [Fact]
    public void The_backup_is_readable_by_its_owner_alone()
    {
        // It holds every project's App key and is written outside boson's own
        // 0700 directory, usually somewhere other users can read.
        if (OperatingSystem.IsWindows()) return;

        UninstallCommand.WriteBackup(_db.Db, _dirs.Paths.TmpDir, Target);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(Target));
    }

    [Fact]
    public void Nothing_is_left_behind_in_the_staging_directory()
    {
        UninstallCommand.WriteBackup(_db.Db, _dirs.Paths.TmpDir, Target);

        Assert.Empty(Directory.GetFiles(_dirs.Paths.TmpDir));
    }

    [Fact]
    public void A_path_that_already_exists_is_refused_and_left_as_it_was()
    {
        File.WriteAllText(Target, "something the operator wanted");

        var e = Assert.Throws<IOException>(() =>
            UninstallCommand.WriteBackup(_db.Db, _dirs.Paths.TmpDir, Target));

        // Refused by boson before the database is read, not by the move after
        // the backup has been staged, whose message says the same words.
        Assert.Equal("that path already exists", e.Message);
        Assert.Equal("something the operator wanted", File.ReadAllText(Target));
        Assert.False(Directory.Exists(_dirs.Paths.TmpDir) && Directory.GetFiles(_dirs.Paths.TmpDir).Length > 0);
    }

    [Fact]
    public void A_directory_at_the_path_is_refused()
    {
        Directory.CreateDirectory(Target);

        var e = Assert.Throws<IOException>(() =>
            UninstallCommand.WriteBackup(_db.Db, _dirs.Paths.TmpDir, Target));

        Assert.Equal("that path already exists", e.Message);
    }
}
