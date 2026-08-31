using System.Text;
using Boson.Util;
using Xunit;

namespace Boson.Tests.Integration;

/// <summary>
/// Spec §20 Caddy --resume row: the regression test for the reboot failure
/// mode. Start Caddy with the platform stack's `caddy run --resume` command,
/// POST a config, restart the container, and assert the config survived —
/// this fails on the stock image command.
/// </summary>
public sealed class CaddyResumeIntegrationTests : IDisposable
{
    private const string ContainerName = "boson-caddy-inttest";
    private const int SitePort = 18124;

    private const string ComposeYaml = $"""
        name: boson-inttest
        services:
          caddy:
            image: caddy:2-alpine
            container_name: {ContainerName}
            network_mode: host
            command: ["caddy", "run", "--resume"]
            volumes:
              - caddy-inttest-data:/data
              - caddy-inttest-config:/config
        volumes:
          caddy-inttest-data:
          caddy-inttest-config:
        """;

    private readonly ProcessRunner _runner = new();
    private readonly DockerCli _docker;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public CaddyResumeIntegrationTests() => _docker = new DockerCli(_runner);

    public void Dispose()
    {
        _docker.ComposeDownStdinAsync(ComposeYaml).GetAwaiter().GetResult();
        _docker.VolumeRemoveAsync("boson-inttest_caddy-inttest-data").GetAwaiter().GetResult();
        _docker.VolumeRemoveAsync("boson-inttest_caddy-inttest-config").GetAwaiter().GetResult();
        _http.Dispose();
    }

    [IntegrationFact]
    public async Task Config_pushed_over_the_admin_api_survives_a_container_restart()
    {
        var up = await _docker.ComposeUpStdinAsync(ComposeYaml);
        Assert.True(up.Ok, $"compose up failed: {up.StdErr}");
        Assert.True(await PollAsync(AdminReachableAsync), "caddy admin API never came up");

        var config = $$"""
            {
              "apps": {
                "http": {
                  "servers": {
                    "test": {
                      "listen": [":{{SitePort}}"],
                      "routes": [
                        {"handle": [{"handler": "static_response", "status_code": 200, "body": "resumed"}]}
                      ]
                    }
                  }
                }
              }
            }
            """;
        var load = await _http.PostAsync("http://127.0.0.1:2019/load",
            new StringContent(config, Encoding.UTF8, "application/json"));
        Assert.True(load.IsSuccessStatusCode,
            $"config load failed: {await load.Content.ReadAsStringAsync()}");
        Assert.True(await PollAsync(SiteAnswersAsync), "site never answered after load");

        var restart = await _runner.RunAsync("docker", ["restart", ContainerName]);
        Assert.True(restart.Ok, $"docker restart failed: {restart.StdErr}");

        Assert.True(await PollAsync(SiteAnswersAsync, seconds: 20),
            "config did not survive the restart — --resume regression");
    }

    private async Task<bool> AdminReachableAsync()
    {
        try
        {
            return (await _http.GetAsync("http://127.0.0.1:2019/config/")).IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> SiteAnswersAsync()
    {
        try
        {
            var response = await _http.GetAsync($"http://127.0.0.1:{SitePort}/");
            return response.IsSuccessStatusCode &&
                   (await response.Content.ReadAsStringAsync()).Contains("resumed");
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> PollAsync(Func<Task<bool>> check, int seconds = 15)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await check()) return true;
            await Task.Delay(500);
        }
        return false;
    }
}
