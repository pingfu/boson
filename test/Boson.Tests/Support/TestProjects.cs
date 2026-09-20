using Boson.Deploy;
using Boson.Storage;

namespace Boson.Tests.Support;

public static class TestProjects
{
    public const string Repo = "acme/site";
    public const string Hostname = "site.example.com";
    public const string Branch = "main";

    public static Project New(
        string repo = Repo,
        string secret = "s3cret",
        string? pem = null) => new()
    {
        Repo = repo,
        GithubAppId = 1234,
        GithubAppSlug = "boson-site",
        GithubInstallationId = 5678,
        GithubWebhookSecret = secret,
        // Most tests never parse this; pass a real key when the code under test does.
        GithubAppPem = pem ?? "-----BEGIN RSA PRIVATE KEY-----\nfake\n-----END RSA PRIVATE KEY-----",
    };

    public static Deployment Deployment(
        long projectId = 1,
        string repo = Repo,
        string branch = Branch,
        string hostname = Hostname,
        string aliases = "",
        int port = 30000,
        string? envSet = null,
        string? expireAfter = null) => new()
    {
        ProjectId = projectId,
        Repo = repo,
        Branch = branch,
        Label = BranchLabel.From(branch),
        Hostname = hostname,
        Aliases = aliases,
        HostPort = port,
        EnvSet = envSet,
        ExpireAfter = expireAfter,
    };

    /// <summary>A project with one deployment, which is what most tests want.</summary>
    public static Deployment Insert(
        IProjectsRepository projects,
        IDeploymentsRepository deployments,
        string repo = Repo,
        string branch = Branch,
        string hostname = Hostname,
        string aliases = "",
        int port = 30000,
        string? envSet = null,
        string? expireAfter = null)
    {
        var projectId = projects.GetByRepo(repo)?.Id ?? projects.Insert(New(repo));

        var id = deployments.Insert(Deployment(
            projectId, repo, branch, hostname, aliases, port, envSet, expireAfter));

        return deployments.Get(id)!;
    }
}
