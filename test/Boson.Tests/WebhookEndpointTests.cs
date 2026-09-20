using System.Security.Cryptography;
using System.Text;
using Boson.Deploy;
using Boson.Serve;
using Boson.Storage;
using Boson.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Boson.Tests;

public sealed class WebhookEndpointTests : IAsyncLifetime
{
    private const string Repo = "acme/site";
    private const string Secret = "s3cret";
    private const string Url = "/_boson/webhook/acme/site";

    private readonly TempDb _db = new();
    private readonly DeploymentLocks _locks = new();
    private readonly FakeDeployer _deployer = new();
    private ProjectsRepository _projects = null!;
    private DeploymentsRepository _deployments = null!;
    private Deployment _deployment = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _projects = new ProjectsRepository(_db.Db);
        _deployments = new DeploymentsRepository(_db.Db);
        _projects.Insert(TestProjects.New(repo: Repo, secret: Secret));
        _deployment = TestProjects.Insert(_projects, _deployments, repo: Repo);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        WebhookEndpoint.Map(_app, _projects, _deployments, _locks, _deployer, NullLogger.Instance);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _db.Dispose();
    }

    private static string Sign(byte[] body, string secret) =>
        "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    private Task<HttpResponseMessage> PostAsync(
        string url, string body, string? secret = Secret, string eventName = "push",
        string? explicitSignature = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Headers.Add("X-GitHub-Event", eventName);
        var signature = explicitSignature ?? (secret is null ? null : Sign(bytes, secret));
        if (signature is not null)
            request.Headers.Add("X-Hub-Signature-256", signature);
        return _client.SendAsync(request);
    }

    private static string PushBody(string branch = "main") =>
        $$"""{"ref":"refs/heads/{{branch}}","after":"abc123"}""";

    private void Activate() => _deployments.MarkWebhookActive(_deployment.Id);

    private Deployment Current() => _deployments.GetByBranch(Repo, "main")!;

    [Fact]
    public async Task Oversized_body_is_413()
    {
        var response = await PostAsync(Url, new string('x', 26 * 1024 * 1024), secret: null);
        Assert.Equal(413, (int)response.StatusCode);
    }

    [Fact]
    public async Task Unknown_project_is_404()
    {
        var response = await PostAsync("/_boson/webhook/acme/nope", PushBody(), secret: null);
        Assert.Equal(404, (int)response.StatusCode);
    }

    [Fact]
    public async Task Archived_project_is_404()
    {
        _projects.Archive(Repo);
        var response = await PostAsync(Url, PushBody());
        Assert.Equal(404, (int)response.StatusCode);
    }

    [Fact]
    public async Task Missing_signature_is_403()
    {
        var response = await PostAsync(Url, PushBody(), secret: null);
        Assert.Equal(403, (int)response.StatusCode);
    }

    [Fact]
    public async Task Wrong_secret_is_403()
    {
        var response = await PostAsync(Url, PushBody(), secret: "wrong");
        Assert.Equal(403, (int)response.StatusCode);
    }

    [Fact]
    public async Task Malformed_signature_hex_is_403()
    {
        var response = await PostAsync(Url, PushBody(), explicitSignature: "sha256=zz-not-hex");
        Assert.Equal(403, (int)response.StatusCode);
    }

    [Fact]
    public async Task Ping_answers_200_pong()
    {
        var response = await PostAsync(Url, """{"zen":"Design for failure."}""", eventName: "ping");
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Contains("pong", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Malformed_payload_with_valid_signature_is_400()
    {
        var response = await PostAsync(Url, """{"not_a_ref":true}""");
        Assert.Equal(400, (int)response.StatusCode);
    }

    [Fact]
    public async Task A_tag_or_other_non_branch_ref_is_200_ignored()
    {
        Activate();
        var response = await PostAsync(Url, """{"ref":"refs/tags/v1","after":"abc123"}""");
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Contains("ignored", await response.Content.ReadAsStringAsync());
        Assert.Empty(_deployer.Calls);
    }

    [Fact]
    public async Task A_branch_with_no_deployment_reaches_the_deployer_to_be_discovered()
    {
        // Only `_boson.yml` can say whether a branch deploys, and only the
        // fetch can read it, so this is not a decision the webhook can make.
        var response = await PostAsync(Url, PushBody(branch: "feature/x"));

        Assert.Equal(202, (int)response.StatusCode);

        await _deployer.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("feature/x", Assert.Single(_deployer.Calls).Branch);
    }

    [Fact]
    public async Task Inactive_idle_deployment_is_200_inactive()
    {
        var response = await PostAsync(Url, PushBody());
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Contains("inactive", await response.Content.ReadAsStringAsync());
        Assert.Empty(_deployer.Calls);
        Assert.False(Current().DeployPending);
    }

    [Fact]
    public async Task Inactive_with_first_deploy_in_flight_is_202_and_sets_pending()
    {
        _locks.TryEnter(Deployer.LockKey(Repo, "main"));
        var response = await PostAsync(Url, PushBody());
        Assert.Equal(202, (int)response.StatusCode);
        Assert.True(Current().DeployPending);
        Assert.Empty(_deployer.Calls);
    }

    [Fact]
    public async Task Valid_push_is_202_and_invokes_the_deployer()
    {
        Activate();
        var response = await PostAsync(Url, PushBody());
        Assert.Equal(202, (int)response.StatusCode);
        Assert.Contains("queued", await response.Content.ReadAsStringAsync());

        await _deployer.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var call = Assert.Single(_deployer.Calls);
        Assert.Equal(Repo, call.Repo);
        Assert.Equal("main", call.Branch);
        Assert.Equal(DeployTrigger.Webhook, call.Trigger);
    }

}
