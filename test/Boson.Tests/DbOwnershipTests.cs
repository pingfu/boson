using Boson.Storage;
using Boson.Tests.Support;
using Xunit;

namespace Boson.Tests;

public class DbOwnershipTests
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [Fact]
    public void The_database_is_closed_to_everyone_but_its_owner()
    {
        // SQLite creates the file with whatever the umask allows, and it holds
        // every project's App key.
        if (OperatingSystem.IsWindows()) return;

        using var db = new TempDb();

        File.SetUnixFileMode(db.DbPath, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        DbOwnership.RestrictMode(db.DbPath);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(db.DbPath));
    }

    [Fact]
    public void A_database_that_does_not_exist_yet_is_not_an_error()
    {
        DbOwnership.RestrictMode(Path.Combine(Path.GetTempPath(), $"boson-absent-{Guid.NewGuid():N}.db"));
    }
}
