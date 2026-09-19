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
    [InlineData("acme/site", "acme-site")]
    [InlineData("acme/my_app", "acme-my_app")]
    [InlineData("acme/app.v2", "acme-app-v2")]
    public void Compose_project_name_replaces_disallowed_characters(string repo, string expected) =>
        Assert.Equal(expected, RepoName.ComposeProjectName(repo));
}
