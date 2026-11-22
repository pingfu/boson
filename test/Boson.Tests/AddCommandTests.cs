using Boson.Commands;
using Xunit;

namespace Boson.Tests;

public class AddCommandTests
{
    [Fact]
    public void The_repository_is_all_add_needs()
    {
        // Hostnames, branches and environment sets are in the repository's own
        // _boson.yml, which add clones to read.
        var result = AddCommand.Create().Parse(["org/app"]);

        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("org", true)]
    [InlineData("user", true)]
    [InlineData("team", false)]
    public void The_owner_is_an_org_or_a_user(string owner, bool accepted)
    {
        var result = AddCommand.Create().Parse(["org/app", "--owner", owner]);

        Assert.Equal(accepted, result.Errors.Count == 0);
    }

    [Fact]
    public void A_saved_project_that_failed_to_declare_is_told_what_it_has_and_how_to_go_on()
    {
        var lines = AddCommand.DeclareFailedLines("org/app", "hostname already in use", []).ToList();

        Assert.Contains(lines, l => l.Contains("hostname already in use"));
        Assert.Contains(lines, l => l.Contains("org/app and its GitHub App are saved"));
        Assert.Contains(lines, l => l.Contains("boson deploy org/app"));
        Assert.Contains(lines, l => l.Contains("boson remove org/app --purge"));
        Assert.DoesNotContain(lines, l => l.Contains("nothing was persisted"));
    }
}
