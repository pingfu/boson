using Boson.Commands;
using Boson.Tests.Support;
using Boson.Util;
using Xunit;

namespace Boson.Tests;

public class InitCommandTests
{
    private static readonly string[] Directories = ["/var/lib/boson", "/var/log/boson", "/srv"];

    private static MapProcessRunner Runner(ProcessResult chown, ProcessResult chmod) => new()
    {
        Map = { ["chown -R"] = chown, ["chmod 0700"] = chmod },
    };

    private static ProcessResult Ok() => new(0, "", "");

    [Fact]
    public async Task Directories_handed_over_and_closed_report_nothing()
    {
        Assert.Null(await InitCommand.SecureDirectoriesAsync(Runner(Ok(), Ok()), Directories));
    }

    [Fact]
    public async Task A_chown_that_fails_stops_init_naming_the_directory()
    {
        var problem = await InitCommand.SecureDirectoriesAsync(
            Runner(new ProcessResult(1, "", "Operation not permitted"), Ok()), Directories);

        Assert.Contains("/var/lib/boson", problem);
        Assert.Contains("Operation not permitted", problem);
    }

    [Fact]
    public async Task The_path_chown_named_is_reported_with_what_to_do_next()
    {
        // init is the recovery command too, so a stop has to say where.
        var problem = await InitCommand.SecureDirectoriesAsync(
            Runner(new ProcessResult(1, "",
                "chown: changing ownership of '/srv/a/locked': Operation not permitted\n"), Ok()),
            Directories);

        Assert.Contains("/srv/a/locked", problem);
        Assert.Contains("re-run boson init", problem);
    }

    [Fact]
    public async Task A_chmod_that_fails_stops_init_rather_than_leaving_secrets_readable()
    {
        // The data directory holds every project's App key. Left at whatever
        // mode it had, any local user reads them, and init would have said ✓.
        var problem = await InitCommand.SecureDirectoriesAsync(
            Runner(Ok(), new ProcessResult(1, "", "Read-only file system")), Directories);

        Assert.Contains("mode 0700", problem);
        Assert.Contains("Read-only file system", problem);
    }
}
