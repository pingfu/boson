using Boson.Commands;
using Xunit;

namespace Boson.Tests;

public class ListCommandTests
{
    [Fact]
    public void Compose_ps_json_lines_are_summarised_by_state()
    {
        const string stdout = """
            {"Name":"acme-site-web-1","State":"running"}
            {"Name":"acme-site-db-1","State":"running"}
            {"Name":"acme-site-job-1","State":"exited"}
            """;
        Assert.Equal("exited(1), running(2)", ListCommand.SummariseComposePs(stdout));
    }

    [Fact]
    public void Compose_ps_json_array_is_summarised_by_state()
    {
        const string stdout = """[{"Name":"a","State":"running"}]""";
        Assert.Equal("running(1)", ListCommand.SummariseComposePs(stdout));
    }

    [Fact]
    public void Empty_output_means_no_containers()
    {
        Assert.Equal("none", ListCommand.SummariseComposePs(""));
        Assert.Equal("none", ListCommand.SummariseComposePs("   \n  "));
    }

    [Fact]
    public void Garbage_output_is_unknown()
    {
        Assert.Equal("unknown", ListCommand.SummariseComposePs("not json at all"));
    }
}
