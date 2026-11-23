using Boson.Deploy;
using Xunit;

namespace Boson.Tests;

public class ComposeImagesTests
{
    [Fact]
    public void A_built_service_reports_the_image_it_produces()
    {
        var images = ComposeImages.Read("""
            {"services":{"web":{"build":{},"image":"acme/site-web:abc123"}}}
            """);

        var image = Assert.Single(images);
        Assert.Equal("web", image.Service);
        Assert.Equal("acme/site-web", image.Name);
        Assert.Equal("abc123", image.Tag);
    }

    [Fact]
    public void A_service_that_only_runs_someone_elses_image_is_not_ours_to_retain()
    {
        Assert.Empty(ComposeImages.Read("""{"services":{"db":{"image":"postgres:17"}}}"""));
    }

    [Fact]
    public void A_registry_port_is_not_read_as_a_tag()
    {
        var image = Assert.Single(ComposeImages.Read("""
            {"services":{"web":{"build":{},"image":"registry.example.com:5000/site"}}}
            """));

        Assert.Equal("registry.example.com:5000/site", image.Name);
        Assert.Equal("latest", image.Tag);
    }

    [Fact]
    public void A_digest_is_somebody_elses_immutable_image()
    {
        Assert.Empty(ComposeImages.Read("""
            {"services":{"web":{"build":{},"image":"acme/site@sha256:aaaa"}}}
            """));
    }

    [Fact]
    public void What_a_deployment_built_and_no_longer_keeps_is_removable()
    {
        var removable = ComposeImages.Removable(["new", "old1", "old2", "old3"], ["new", "old1"], []);

        Assert.Equal(["old2", "old3"], removable.Order());
    }

    [Fact]
    public void A_commit_another_deployment_keeps_is_not_removable()
    {
        var removable = ComposeImages.Removable(
            ["new", "old1", "old2", "old3"], ["new"], [["old3"], ["old2"]]);

        Assert.Equal(["old1"], removable);
    }

    [Fact]
    public void A_commit_this_deployment_never_built_is_not_removable_whoever_keeps_it()
    {
        var removable = ComposeImages.Removable(["new", "old1"], ["new"], [["theirs"]]);

        Assert.Equal(["old1"], removable);
    }

    [Fact]
    public void A_torn_down_deployment_keeps_none_of_its_own()
    {
        var removable = ComposeImages.Removable(["new", "old1"], [], [["old1"]]);

        Assert.Equal(["new"], removable);
    }
}
