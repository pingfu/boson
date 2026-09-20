using Boson.Deploy;
using Boson.Storage;
using Boson.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Boson.Tests;

public sealed class ExpiryReaperTests : IDisposable
{
    private readonly TempDb _db = new();
    private readonly ProjectsRepository _projects;
    private readonly DeploymentsRepository _deployments;
    private readonly DeploysRepository _deploys;
    private readonly FakeDeployer _deployer = new();
    private readonly MutableClock _clock = new();

    public ExpiryReaperTests()
    {
        _projects = new ProjectsRepository(_db.Db);
        _deployments = new DeploymentsRepository(_db.Db);
        _deploys = new DeploysRepository(_db.Db);
    }

    public void Dispose() => _db.Dispose();

    private ExpiryReaper NewReaper() =>
        new(_deployments, _deploys, _deployer, _clock, NullLogger.Instance);

    private void Deployed(Deployment deployment, bool succeeded = true)
    {
        var id = _deploys.Insert(deployment.Id, DeployTrigger.Webhook, i => $"{i}.log");
        _deploys.Finish(id, succeeded, succeeded ? null : "build failed");
    }

    [Theory]
    [InlineData("14d", 14 * 24)]
    [InlineData("36h", 36)]
    [InlineData("90m", 1.5)]
    public void Durations_read_in_days_hours_and_minutes(string text, double hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), ExpiryReaper.ParseDuration(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("0d")]
    [InlineData("-3d")]
    [InlineData("7w")]
    public void Anything_else_is_ignored_rather_than_guessed_at(string? text)
    {
        Assert.Null(ExpiryReaper.ParseDuration(text));
    }

    [Fact]
    public async Task A_deployment_with_no_expiry_is_left_alone()
    {
        var deployment = TestProjects.Insert(_projects, _deployments);
        Deployed(deployment);

        _clock.Now += TimeSpan.FromDays(365);
        await NewReaper().SweepAsync();

        Assert.Empty(_deployer.TearDowns);
    }

    [Fact]
    public async Task An_idle_deployment_past_its_window_is_stopped()
    {
        var deployment = TestProjects.Insert(_projects, _deployments, expireAfter: "7d");
        Deployed(deployment);

        _clock.Now += TimeSpan.FromDays(8);
        await NewReaper().SweepAsync();

        Assert.Equal(deployment.Id, Assert.Single(_deployer.TearDowns).Id);
    }

    [Fact]
    public async Task A_deployment_inside_its_window_keeps_running()
    {
        var deployment = TestProjects.Insert(_projects, _deployments, expireAfter: "7d");
        Deployed(deployment);

        _clock.Now += TimeSpan.FromDays(6);
        await NewReaper().SweepAsync();

        Assert.Empty(_deployer.TearDowns);
    }

    [Fact]
    public async Task Failing_deploys_do_not_keep_a_deployment_alive()
    {
        // Idleness is measured from the last deploy that worked: a branch whose
        // recent deploys all failed is not one somebody is using.
        var deployment = TestProjects.Insert(_projects, _deployments, expireAfter: "7d");
        Deployed(deployment);

        _clock.Now += TimeSpan.FromDays(8);
        Deployed(deployment, succeeded: false);

        await NewReaper().SweepAsync();

        Assert.Single(_deployer.TearDowns);
    }

    [Fact]
    public async Task A_deployment_that_never_deployed_expires_from_when_it_was_created()
    {
        var deployment = TestProjects.Insert(_projects, _deployments, expireAfter: "7d");

        _clock.Now += TimeSpan.FromDays(8);
        await NewReaper().SweepAsync();

        Assert.Equal(deployment.Id, Assert.Single(_deployer.TearDowns).Id);
    }
}
