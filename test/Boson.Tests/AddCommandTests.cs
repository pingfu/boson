using Boson.Commands;
using Xunit;

namespace Boson.Tests;

public class AddCommandTests
{
    [Fact]
    public void Repo_and_hostname_are_all_add_needs()
    {
        var result = AddCommand.Create().Parse(["org/app", "--hostname", "app.example.com"]);

        Assert.Empty(result.Errors);
    }
}
