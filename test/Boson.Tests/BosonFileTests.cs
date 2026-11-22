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
    public void Hostnames_are_lowercased_because_dns_is_case_insensitive()
    {
        // What the file says becomes a row, a Caddy matcher and a certificate
        // subject, and DNS considers these one name.
        var file = BosonFile.Parse("""
            version: 1
            deployments:
              - branch: main
                hostname: Example.ORG
                aliases: [WWW.Example.org]
            """, out _)!;

        Assert.Equal("example.org", file.Deployments[0].Hostname);
        Assert.Equal(["www.example.org"], file.Deployments[0].Aliases);
    }

    [Fact]
    public void A_name_claimed_twice_in_different_case_is_still_claimed_twice()
    {
        Assert.Null(BosonFile.Parse("""
            version: 1
            deployments:
              - branch: main
                hostname: example.org
              - branch: develop
                hostname: EXAMPLE.ORG
            """, out var problem));

        Assert.Contains("claimed by both", problem);
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
            $"{BranchSlug.From("feature/search")}.preview.example.org",
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

    private static string WithEntryLine(string line) =>
        $"version: 1\ndeployments:\n  - branch: main\n    hostname: a.example.org\n    {line}";

    [Theory]
    [InlineData("../other/production")]
    [InlineData("/etc/passwd")]
    [InlineData("nested/production")]
    [InlineData("nested\\\\production")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("\"\"")]
    [InlineData("\"with space\"")]
    public void An_env_that_is_not_a_plain_file_name_is_refused(string env)
    {
        // The name is joined onto the project's own environment directory, so
        // one that climbs out of it reads another project's secrets.
        Assert.Null(BosonFile.Parse(WithEntryLine($"env: {env}"), out var problem));
        Assert.Contains("an environment set is a file name", problem);
    }

    [Theory]
    [InlineData("production")]
    [InlineData("preview-2")]
    [InlineData("eu_west.prod")]
    public void A_plain_file_name_is_an_env(string env)
    {
        Assert.Equal(env, BosonFile.Parse(WithEntryLine($"env: {env}"), out _)!.Deployments[0].Env);
    }

    [Theory]
    [InlineData("2w")]
    [InlineData("soon")]
    [InlineData("14")]
    [InlineData("0d")]
    [InlineData("-3d")]
    [InlineData("99999999d")]
    public void An_expiry_boson_cannot_read_is_refused_rather_than_ignored(string expireAfter)
    {
        // Ignored, a typo is a deployment that never expires and never says so.
        Assert.Null(BosonFile.Parse(WithEntryLine($"expire_after: {expireAfter}"), out var problem));
        Assert.Contains($"expire_after for main is \"{expireAfter}\"", problem);
    }

    [Theory]
    [InlineData("version: 1\ndeployments:\n", "no deployments")]
    [InlineData("version: 1\ndeployments:\n  -\n", "empty entry")]
    [InlineData("version: 1\ndeployments:\n  - branch: main\n    hostname: a.example.org\n    aliases: [~]", "empty alias")]
    public void A_key_with_nothing_after_it_is_a_problem_with_words_not_a_null_reference(
        string yaml, string expected)
    {
        Assert.Null(BosonFile.Parse(yaml, out var problem));
        Assert.Contains(expected, problem);
    }

    [Theory]
    [InlineData("aliases:")]
    [InlineData("env:")]
    [InlineData("expire_after:")]
    public void An_optional_key_left_empty_is_the_same_as_leaving_it_out(string line)
    {
        var file = BosonFile.Parse(WithEntryLine(line), out var problem);

        Assert.Null(problem);
        Assert.Empty(file!.Deployments[0].Aliases);
        Assert.Null(file.Deployments[0].Env);
        Assert.Null(file.Deployments[0].ExpireAfter);
    }

    [Fact]
    public void Images_left_empty_keeps_the_default()
    {
        var file = BosonFile.Parse(WithEntryLine("aliases: []") + "\nimages:\n", out var problem);

        Assert.Null(problem);
        Assert.Equal(3, file!.Images.Keep);
    }

    [Fact]
    public void Unparseable_yaml_carries_the_parser_message()
    {
        Assert.Null(BosonFile.Parse("deployments: [ unclosed", out var problem));
        Assert.Contains("does not parse", problem);
    }
}
