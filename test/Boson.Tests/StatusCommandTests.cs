using Boson.Commands;
using Xunit;

namespace Boson.Tests;

public class StatusCommandTests
{
    [Fact]
    public void Compose_ps_json_lines_are_summarised_by_state()
    {
        const string stdout = """
            {"Name":"acme-site-web-1","State":"running"}
            {"Name":"acme-site-db-1","State":"running"}
            {"Name":"acme-site-job-1","State":"exited"}
            """;
        Assert.Equal("exited(1), running(2)", StatusCommand.SummariseComposePs(stdout));
    }

    [Fact]
    public void Compose_ps_json_array_is_summarised_by_state()
    {
        const string stdout = """[{"Name":"a","State":"running"}]""";
        Assert.Equal("running(1)", StatusCommand.SummariseComposePs(stdout));
    }

    [Fact]
    public void Empty_output_means_no_containers()
    {
        Assert.Equal("none", StatusCommand.SummariseComposePs(""));
        Assert.Equal("none", StatusCommand.SummariseComposePs("   \n  "));
    }

    [Fact]
    public void Garbage_output_is_unknown()
    {
        Assert.Equal("unknown", StatusCommand.SummariseComposePs("not json at all"));
    }

    [Fact]
    public void Version_keeps_a_short_commit_prefix()
    {
        Assert.Equal("0.1.13+9ef6a3b",
            StatusCommand.ShortenVersion("0.1.13+9ef6a3b5b19af7bba064f3a798abe8284c7880fc"));
        Assert.Equal("0.1.13", StatusCommand.ShortenVersion("0.1.13"));
    }

    [Fact]
    public void A_silent_daemon_names_the_command_that_diagnoses_it()
    {
        Assert.Contains("systemctl status boson", StatusCommand.DescribeDaemon(null, Now));
    }

    [Fact]
    public void A_daemon_older_than_the_binary_names_the_restart()
    {
        var text = StatusCommand.DescribeDaemon(
            new StatusCommand.DaemonInfo("0.0.1+deadbee", Now.AddHours(-3)), Now);

        Assert.Contains("0.0.1+deadbee", text);
        Assert.Contains("up 3h 0m", text);
        Assert.Contains("systemctl restart boson", text);
    }

    [Theory]
    [InlineData(0.5, "30m")]
    [InlineData(5, "5h 0m")]
    [InlineData(50, "2d 2h")]
    public void Uptime_reads_in_the_largest_two_units(double hours, string expected)
    {
        Assert.Equal(expected, StatusCommand.Uptime(TimeSpan.FromHours(hours)));
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(1, "1 minute ago")]
    [InlineData(90, "1 hour ago")]
    [InlineData(60 * 24 * 8, "8 days ago")]
    [InlineData(60 * 24 * 70, "2 months ago")]
    public void Age_rounds_down_to_one_unit(int minutesAgo, string expected)
    {
        Assert.Equal(expected, StatusCommand.Ago(Now.AddMinutes(-minutesAgo), Now));
    }

    [Fact]
    public void A_stored_timestamp_is_read_as_utc_and_shown_with_its_age()
    {
        var text = StatusCommand.Timestamp("2026-09-01 08:32:10", Now);

        Assert.Equal("2026-09-01 08:32:10 (18 days ago)", text);
    }

    [Fact]
    public void An_unparseable_timestamp_is_passed_through()
    {
        Assert.Equal("whenever", StatusCommand.Timestamp("whenever", Now));
        Assert.Equal("", StatusCommand.Timestamp(null, Now));
    }

    private static readonly DateTimeOffset Now =
        new(2026, 9, 19, 21, 0, 0, TimeSpan.Zero);
}
