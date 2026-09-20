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
}
