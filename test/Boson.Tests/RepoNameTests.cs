using Xunit;

namespace Boson.Tests;

public class RepoNameTests
{
    [Theory]
    [InlineData("Acme/Site", "acme/site")]
    [InlineData("  acme/site  ", "acme/site")]
    [InlineData("acme/site/", "acme/site")]
    [InlineData("acme6/widget-store", "acme6/widget-store")]
    public void Canonicalises_valid_repos(string input, string expected)
    {
        Assert.True(RepoName.TryCanonicalise(input, out var repo));
        Assert.Equal(expected, repo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nослash")]
    [InlineData("noslash")]
    [InlineData("a/b/c")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("has space/repo")]
    public void Rejects_invalid_repos(string input) =>
        Assert.False(RepoName.TryCanonicalise(input, out _));

    [Fact]
    public void Splits_org_and_name()
    {
        var (org, name) = RepoName.Split("acme/site");
        Assert.Equal("acme", org);
        Assert.Equal("site", name);
    }

    [Theory]
    [InlineData("acme/site", "main", "acme-site-main")]
    [InlineData("acme/my_app", "main", "acme-my_app-main")]
    [InlineData("acme/app.v2", "main", "acme-app-v2-main")]
    // Two branches of one repository must not share a compose project, or one
    // deploy tears down the other's containers.
    [InlineData("acme/site", "feature-x", "acme-site-feature-x")]
    public void Compose_project_name_replaces_disallowed_characters(
        string repo, string label, string expected) =>
        Assert.Equal(expected, RepoName.ComposeProjectName(repo, label));
}
