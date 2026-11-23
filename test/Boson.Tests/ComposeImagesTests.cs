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

    [Theory]
    [InlineData(3, new[] { "old3" })]
    [InlineData(2, new[] { "old2", "old3" })]
    [InlineData(1, new[] { "old1", "old2", "old3" })]
    public void Retention_counts_the_build_just_made(int keep, string[] expected)
    {
        var removable = ComposeImages.Removable(["new", "old1", "old2", "old3"], keep);

        Assert.Equal(expected, removable.Order());
    }

    [Fact]
    public void A_commit_deployed_twice_counts_once()
    {
        // A redeploy of the same commit is one image, not two of the allowance.
        var removable = ComposeImages.Removable(["new", "new", "old1", "old2"], keep: 2);

        Assert.Equal(["old2"], removable);
    }
}
