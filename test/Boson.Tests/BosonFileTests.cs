using Boson.Deploy;
using Xunit;

namespace Boson.Tests;

public class BosonFileTests
{
    private const string Valid = """
        version: 1
        deployments:
          - branch: main
            hostname: example.org
            aliases: [www.example.org]
            env: production
          - branch: "feature/*"
            hostname: "{branch}.preview.example.org"
            env: preview
            expire_after: 7d
        images:
          keep: 5
        """;

    [Fact]
    public void A_valid_file_parses_into_its_entries()
    {
        var file = BosonFile.Parse(Valid, out var problem);

        Assert.Null(problem);
        Assert.NotNull(file);
        Assert.Equal(2, file.Deployments.Count);
        Assert.Equal("production", file.Deployments[0].Env);
        Assert.Equal(["www.example.org"], file.Deployments[0].Aliases);
        Assert.Equal("7d", file.Deployments[1].ExpireAfter);
        Assert.Equal(5, file.Images.Keep);
    }

    [Fact]
    public void Keep_defaults_when_the_file_says_nothing_about_images()
    {
        var file = BosonFile.Parse("""
            version: 1
            deployments:
              - branch: main
                hostname: example.org
            """, out _)!;

        Assert.Equal(3, file.Images.Keep);

        // Null, not "default": naming a set means the file must exist, and a
        // project that needs no environment file names none.
        Assert.Null(file.Deployments[0].Env);
    }

    [Theory]
    [InlineData("main", "main")]
    [InlineData("feature/search", "feature/*")]
    [InlineData("feature/a/b", "feature/*")]
    public void The_first_matching_entry_wins(string branch, string expected)
    {
        Assert.Equal(expected, BosonFile.Parse(Valid, out _)!.Match(branch)?.Branch);
    }

    [Fact]
    public void A_branch_no_entry_matches_has_no_deployment()
    {
        Assert.Null(BosonFile.Parse(Valid, out _)!.Match("develop"));
    }

    [Fact]
    public void A_wildcard_stands_for_something()
    {
        var entry = BosonFile.Parse(Valid, out _)!.Deployments[1];

        Assert.False(entry.Matches("feature/"));
        Assert.True(entry.Matches("feature/x"));
    }

    [Fact]
    public void The_branch_fills_the_placeholder_in_a_pattern_hostname()
    {
        var entry = BosonFile.Parse(Valid, out _)!.Deployments[1];

        Assert.Equal(
            $"{BranchLabel.From("feature/search")}.preview.example.org",
            entry.HostnameFor("feature/search"));
    }

    [Theory]
    [InlineData("version: 2\ndeployments:\n  - branch: main\n    hostname: a.example.org", "version 2")]
    [InlineData("version: 1\ndeployments: []", "no deployments")]
    [InlineData("version: 1\ndeployments:\n  - hostname: a.example.org", "no branch")]
    [InlineData("version: 1\ndeployments:\n  - branch: main", "no hostname")]
    [InlineData("version: 1\ndeployments:\n  - branch: main\n    hostname: a.example.org\n  - branch: dev\n    hostname: a.example.org", "claimed by both")]
    [InlineData("version: 1\ndeployments:\n  - branch: \"*\"\n    hostname: a.example.org", "needs {branch}")]
    [InlineData("version: 1\ndeployments:\n  - branch: main\n    hostname: \"{branch}.example.org\"", "nothing to vary")]
    [InlineData("version: 1\ndeployments:\n  - branch: main\n    hostname: a.example.org\nimages:\n  keep: 0", "at least one image")]
    public void A_file_that_cannot_be_honoured_says_why(string yaml, string expected)
    {
        var file = BosonFile.Parse(yaml, out var problem);

        Assert.Null(file);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void Unparseable_yaml_carries_the_parser_message()
    {
        Assert.Null(BosonFile.Parse("deployments: [ unclosed", out var problem));
        Assert.Contains("does not parse", problem);
    }
}
