using Boson.Platform;
using Boson.Tests.Support;
using Boson.Util;
using Xunit;

namespace Boson.Tests;

public class PreflightTests
{
    private sealed class StubProbe : IHostProbe
    {
        public bool IsRoot { get; set; } = true;
        public bool HasSystemd { get; set; } = true;
    }

    private static MapProcessRunner HealthyRunner() => new()
    {
        Map =
        {
            ["docker info"] = new ProcessResult(0, "", ""),
            ["docker compose"] = new ProcessResult(0, "2.27.0", ""),
            ["git --version"] = new ProcessResult(0, "git version 2.44.0", ""),
        },
    };

    [Fact]
    public async Task Healthy_host_has_no_failures()
    {
        var failures = await new Preflight(HealthyRunner(), new StubProbe()).RunAsync();
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Missing_git_is_fatal_with_remediation()
    {
        var runner = HealthyRunner();
        runner.Map.Remove("git --version");

        var failures = await new Preflight(runner, new StubProbe()).RunAsync();

        var failure = Assert.Single(failures);
        Assert.Contains("git", failure.Problem);
        Assert.Contains("apt-get install -y git", failure.Remedy);
    }

    [Fact]
    public async Task All_failures_are_reported_together_not_just_the_first()
    {
        var runner = new MapProcessRunner(); // nothing on PATH
        var probe = new StubProbe { IsRoot = false, HasSystemd = false };

        var failures = await new Preflight(runner, probe).RunAsync();

        Assert.Equal(5, failures.Count);
        Assert.Contains(failures, f => f.Problem.Contains("docker engine"));
        Assert.Contains(failures, f => f.Problem.Contains("compose"));
        Assert.Contains(failures, f => f.Problem.Contains("root"));
        Assert.Contains(failures, f => f.Problem.Contains("git"));
        Assert.Contains(failures, f => f.Problem.Contains("systemd"));
    }

    [Fact]
    public async Task Compose_v1_fails_the_version_gate()
    {
        var runner = HealthyRunner();
        runner.Map["docker compose"] = new ProcessResult(0, "1.29.2", "");

        var failures = await new Preflight(runner, new StubProbe()).RunAsync();

        var failure = Assert.Single(failures);
        Assert.Contains("compose", failure.Problem);
    }

    [Fact]
    public async Task Compose_version_with_v_prefix_passes()
    {
        var runner = HealthyRunner();
        runner.Map["docker compose"] = new ProcessResult(0, "v2.24.5-desktop.1", "");

        var failures = await new Preflight(runner, new StubProbe()).RunAsync();
        Assert.Empty(failures);
    }

    [Fact]
    public void Format_lists_every_failure_and_states_no_changes_were_made()
    {
        var text = Preflight.Format([
            new PreflightFailure("git not found on PATH", "Required.", "apt-get install -y git"),
            new PreflightFailure("docker compose v2 plugin not found", "Required.", "apt-get install -y docker-compose-plugin"),
        ]);

        Assert.Contains("2 problems", text);
        Assert.Contains("git not found on PATH", text);
        Assert.Contains("docker compose v2 plugin not found", text);
        Assert.Contains("No changes were made.", text);
    }
}
