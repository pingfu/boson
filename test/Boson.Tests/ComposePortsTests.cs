using Boson.Deploy;
using Xunit;

namespace Boson.Tests;

public class ComposePortsTests
{
    private static string Config(string hostIp, string published) =>
        """
        {"services":{"web":{"ports":[
          {"mode":"ingress","host_ip":"HOST_IP","target":3000,"published":"PUBLISHED","protocol":"tcp"}
        ]}}}
        """
        .Replace("HOST_IP", hostIp)
        .Replace("PUBLISHED", published);

    [Fact]
    public void Publishing_the_allocated_port_on_loopback_is_accepted()
    {
        Assert.Null(ComposePorts.Problem(Config("127.0.0.1", "30000"), 30000));
    }

    [Fact]
    public void A_numeric_published_port_is_read_too()
    {
        // compose emits this as a number on some versions and a string on others.
        const string config =
            """{"services":{"web":{"ports":[{"host_ip":"127.0.0.1","target":3000,"published":30000}]}}}""";

        Assert.Null(ComposePorts.Problem(config, 30000));
    }

    [Fact]
    public void A_hard_coded_port_is_named_along_with_the_variable_that_replaces_it()
    {
        var problem = ComposePorts.Problem(Config("127.0.0.1", "8080"), 30000);

        Assert.Contains("8080", problem);
        Assert.Contains("30000", problem);
        Assert.Contains("BOSON_PORT", problem);
    }

    [Fact]
    public void Publishing_on_every_interface_is_refused()
    {
        var problem = ComposePorts.Problem(Config("", "30000"), 30000);

        Assert.Contains("every interface", problem);
    }

    [Fact]
    public void Publishing_nothing_is_refused()
    {
        var problem = ComposePorts.Problem("""{"services":{"web":{"image":"nginx"}}}""", 30000);

        Assert.Contains("BOSON_PORT", problem);
    }

    [Fact]
    public void Unreadable_output_is_reported_rather_than_thrown()
    {
        Assert.Contains("could not read", ComposePorts.Problem("not json", 30000));
    }
}
