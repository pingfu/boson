using System.Net;
using Boson.Commands;
using Boson.Serve;
using Xunit;

namespace Boson.Tests;

public class DeployCommandTests
{
    private static RpcResult<DeployStartResponse> Refused(int status, string? error) =>
        new((HttpStatusCode)status, null, error);

    [Fact]
    public void A_started_deploy_is_not_a_refusal()
    {
        Assert.Null(DeployCommand.Refusal(
            new RpcResult<DeployStartResponse>(HttpStatusCode.Accepted, new DeployStartResponse(7), null)));
    }

    [Fact]
    public void A_404_prints_what_the_daemon_said_rather_than_assuming_an_unknown_project()
    {
        var refusal = DeployCommand.Refusal(Refused(404, "acme/site has no deployment for staging"))!.Value;

        Assert.Equal("acme/site has no deployment for staging", refusal.Message);
        Assert.Equal(ExitCodes.UserError, refusal.ExitCode);
    }

    [Fact]
    public void An_unknown_project_is_reported_when_the_daemon_reports_one()
    {
        var refusal = DeployCommand.Refusal(Refused(404, "unknown project: acme/nope"))!.Value;

        Assert.Equal("unknown project: acme/nope", refusal.Message);
    }

    [Fact]
    public void A_409_prints_what_the_daemon_said_rather_than_assuming_a_running_deploy()
    {
        var refusal = DeployCommand.Refusal(Refused(409, "acme/site deploys main, staging; name one with --branch"))!.Value;

        Assert.Equal("acme/site deploys main, staging; name one with --branch", refusal.Message);
        Assert.Equal(ExitCodes.LockContention, refusal.ExitCode);
    }

    [Fact]
    public void A_failed_declaration_prints_its_reason_and_is_a_runtime_failure()
    {
        var refusal = DeployCommand.Refusal(
            Refused(422, "initial fetch failed: could not read acme/site (404): Not Found"))!.Value;

        Assert.Equal("initial fetch failed: could not read acme/site (404): Not Found", refusal.Message);
        Assert.Equal(ExitCodes.RuntimeFailure, refusal.ExitCode);
    }

    [Fact]
    public void A_refusal_with_no_message_names_the_status()
    {
        var refusal = DeployCommand.Refusal(Refused(500, null))!.Value;

        Assert.Equal("deploy request failed (500)", refusal.Message);
    }

    [Fact]
    public void A_declaration_the_daemon_started_is_not_a_refusal()
    {
        Assert.Null(DeployCommand.Refusal(new RpcResult<DeployStartResponse>(
            HttpStatusCode.Accepted, new DeployStartResponse(0, Declaring: true), null)));
    }

    private static DeclareStatusResponse Declaration(string phase, string? error = null, params string[] warnings) =>
        new(phase, error, warnings);

    /// <summary>Answers each poll with the next status, and counts them.</summary>
    private static Func<CancellationToken, Task<RpcResult<DeclareStatusResponse>>> Polls(
        List<DeclareStatusResponse?> statuses, Action? onPoll = null)
    {
        var next = 0;

        return _ =>
        {
            onPoll?.Invoke();

            var body = statuses[Math.Min(next++, statuses.Count - 1)];

            return Task.FromResult(new RpcResult<DeclareStatusResponse>(
                body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK, body, null));
        };
    }

    [Fact]
    public async Task A_declaration_is_followed_for_as_long_as_it_takes()
    {
        var polls = 0;
        var statuses = Enumerable.Repeat<DeclareStatusResponse?>(Declaration("declaring"), 50)
            .Append(Declaration("done"))
            .ToList();
        var output = new StringWriter();
        var error = new StringWriter();

        var declared = await DeployCommand.AwaitDeclarationAsync(
            "acme/site", Polls(statuses, () => polls++), output, error, TimeSpan.Zero, CancellationToken.None);

        Assert.Null(declared.ExitCode);
        Assert.Equal(51, polls);
        Assert.Contains("acme/site has no deployments yet", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task Warnings_from_a_declaration_are_printed_and_carried_to_the_end_of_the_deploy()
    {
        const string warning = "the App's webhook address is still deploy.example.com; pushes will not deploy";
        var error = new StringWriter();

        var declared = await DeployCommand.AwaitDeclarationAsync(
            "acme/site", Polls([Declaration("done", null, warning)]),
            new StringWriter(), error, TimeSpan.Zero, CancellationToken.None);

        Assert.Null(declared.ExitCode);
        Assert.Contains($"⚠ {warning}", error.ToString());
        Assert.Equal([warning], declared.Warnings);
    }

    [Fact]
    public async Task A_failed_declaration_prints_its_reason_and_its_warnings()
    {
        var error = new StringWriter();

        var declared = await DeployCommand.AwaitDeclarationAsync(
            "acme/site",
            Polls([Declaration("failed", "initial fetch failed: network down", "site.example.com does not resolve")]),
            new StringWriter(), error, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(ExitCodes.RuntimeFailure, declared.ExitCode);
        Assert.Contains("initial fetch failed: network down", error.ToString());
        Assert.Contains("⚠ site.example.com does not resolve", error.ToString());
    }

    [Fact]
    public async Task A_daemon_that_forgot_the_declaration_says_to_run_deploy_again()
    {
        var error = new StringWriter();

        var declared = await DeployCommand.AwaitDeclarationAsync(
            "acme/site", Polls([null]), new StringWriter(), error, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(ExitCodes.RuntimeFailure, declared.ExitCode);
        Assert.Contains("re-run boson deploy acme/site", error.ToString());
    }

    [Fact]
    public async Task Ctrl_c_stops_the_watching_and_nothing_else()
    {
        using var cts = new CancellationTokenSource();

        // Cancelling reaches only this loop: the poll is all the CLI ever
        // sends, so there is no request whose abort could stop the daemon.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeployCommand.AwaitDeclarationAsync(
            "acme/site", Polls([Declaration("declaring")], cts.Cancel),
            new StringWriter(), new StringWriter(), TimeSpan.FromMinutes(1), cts.Token));
    }
}
