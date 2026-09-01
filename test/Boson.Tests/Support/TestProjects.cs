using Boson.Storage;

namespace Boson.Tests.Support;

public static class TestProjects
{
    public static Project New(
        string repo = "acme/site",
        string hostname = "site.example.com",
        int port = 8080,
        string branch = "main",
        string secret = "s3cret",
        string? pem = null) => new()
    {
        Repo = repo,
        Hostname = hostname,
        UpstreamPort = port,
        Branch = branch,
        GithubAppId = 1234,
        GithubAppSlug = "boson-site",
        GithubInstallationId = 5678,
        GithubWebhookSecret = secret,
        // Most tests never parse this; pass a real key when the code under test does.
        GithubAppPem = pem ?? "-----BEGIN RSA PRIVATE KEY-----\nfake\n-----END RSA PRIVATE KEY-----",
    };
}
