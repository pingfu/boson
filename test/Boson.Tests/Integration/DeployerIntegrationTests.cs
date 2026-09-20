using Boson.Deploy;
using Boson.Storage;
using Boson.Tests.Support;
using Boson.Util;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Boson.Tests.Integration;

/// <summary>
/// A real `docker compose up -d --build` against a fake compose project (a
/// single nginx container). Git and token minting are faked; the docker path
/// is real.
/// </summary>
public sealed class DeployerIntegrationTests : IDisposable
{
    private const string Repo = "boson-it/web";
    private const int Port = 18123;

    private readonly TempDb _db = new();
    private readonly TempDirs _dirs = new();
    private readonly DockerCli _docker = new(new ProcessRunner());

    public void Dispose()
    {
        _docker.ComposeDownAsync(RepoName.ComposeProjectName(Repo, "main")).GetAwaiter().GetResult();
        _db.Dispose();
        _dirs.Dispose();
    }

    [IntegrationFact]
    public async Task Deploy_builds_and_starts_the_project_and_activates_the_webhook()
    {
        var projects = new ProjectsRepository(_db.Db);
        var deployments = new DeploymentsRepository(_db.Db);
        var deploys = new DeploysRepository(_db.Db);

        var deployment = TestProjects.Insert(projects, deployments,
            repo: Repo, hostname: "it.example.com", port: Port);

        // The fake "fetch" materialises the two files a real clone would have
        // left behind, standing in for git.
        var git = new FakeGitCli
        {
            OnFetchDir = dir =>
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, BosonFile.FileName), """
                    version: 1
                    deployments:
                      - branch: main
                        hostname: it.example.com
                    """);
                File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), """
                    services:
                      web:
                        image: nginx:alpine
                        ports:
                          - "127.0.0.1:${BOSON_PORT}:80"
                    """);
            },
        };

        var deployer = new Deployer(
            projects, deployments, deploys, new FakeMinter(), git, _docker,
            new ImageRetainer(_docker), new FakeCaddySynchroniser(), new DeploymentLocks(),
            _dirs.Paths, NullLogger.Instance);

        var result = await deployer.DeployAsync(Repo, "main", DeployTrigger.Manual);

        var completed = Assert.IsType<DeployResult.Completed>(result);
        Assert.True(completed.Succeeded,
            $"deploy failed: {deploys.Get(completed.LastDeployId)?.Error}");
        Assert.True(deployments.Get(deployment.Id)!.WebhookActive);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var up = false;
        for (var i = 0; i < 20 && !up; i++)
        {
            try
            {
                var response = await http.GetAsync($"http://127.0.0.1:{Port}/");
                up = response.IsSuccessStatusCode;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
        Assert.True(up, "nginx did not answer on the upstream port");
    }
}
