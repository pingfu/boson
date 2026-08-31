using System.CommandLine;
using Boson.Caddy;
using Boson.Platform;
using Boson.Storage;
using Boson.Util;

namespace Boson.Commands;

public static class InitCommand
{
    public static Command Create()
    {
        var hostArg = new Argument<string>("admin-hostname")
        {
            Description = "Public hostname for the platform's /_boson/* endpoints, e.g. deploy.example.com",
        };

        var cmd = new Command("init",
            "Install the platform; re-run rebuilds all derived state (recovery)");
        
        cmd.Arguments.Add(hostArg);
        cmd.SetAction((parseResult, ct) => RunAsync(parseResult.GetValue(hostArg)!, ct));
        
        return cmd;
    }

    public static async Task<int> RunAsync(string adminHostname, CancellationToken ct)
    {
        adminHostname = adminHostname.Trim().TrimEnd('.').ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(adminHostname) || !adminHostname.Contains('.'))
        {
            Console.Error.WriteLine($"invalid admin hostname: {adminHostname}");
            return ExitCodes.UserError;
        }

        var paths = new BosonPaths();
        var runner = new ProcessRunner();
        var docker = new DockerCli(runner);

        // Step 1 — preflight verifies, never installs. Nothing is touched on failure.
        var preflight = new Preflight(runner, new HostProbe());
        var failures = await preflight.RunAsync(ct);

        if (failures.Count > 0)
        {
            Console.Error.WriteLine(Preflight.Format(failures));
            return ExitCodes.UserError;
        }

        using var platformLock = PlatformLock.TryAcquire(paths.PlatformLockPath);

        if (platformLock is null)
        {
            Console.Error.WriteLine("another boson init/uninstall is running");
            return ExitCodes.LockContention;
        }

        // Step 2 — the boson system user.
        var userCheck = await runner.RunAsync("id", ["-u", "boson"], ct: ct);

        if (!userCheck.Ok)
        {
            var useradd = await runner.RunAsync("useradd",
                ["--system", "--no-create-home", "--home-dir", paths.DataDir,
                 "--shell", "/usr/sbin/nologin", "boson"], ct: ct);
            if (!useradd.Ok)
            {
                Console.Error.WriteLine($"failed to create boson user: {useradd.StdErr.Trim()}");
                return ExitCodes.RuntimeFailure;
            }
            Console.WriteLine("✓ created boson system user");
        }

        var usermod = await runner.RunAsync("usermod", ["-aG", "docker", "boson"], ct: ct);
        
        if (!usermod.Ok)
        {
            Console.Error.WriteLine($"failed to add boson to the docker group: {usermod.StdErr.Trim()}");
            return ExitCodes.RuntimeFailure;
        }

        // Step 3 — filesystem; existing trees are re-chowned to the (possibly new) uid.
        foreach (var dir in new[]
                 {
                     paths.DataDir, paths.LocksDir, paths.TmpDir,
                     paths.LogDir, paths.DeployLogsDir, paths.SrvDir,
                 })
            Directory.CreateDirectory(dir);

        foreach (var dir in new[] { paths.DataDir, paths.LogDir, paths.SrvDir })
        {
            await runner.RunAsync("chown", ["-R", "boson:boson", dir], ct: ct);
            await runner.RunAsync("chmod", ["0700", dir], ct: ct);
        }

        Console.WriteLine("✓ directories in place");

        // Step 4 — daemon: unit file, enable, restart; it migrates the DB at startup.
        var unit = new SystemdUnit(runner, paths.UnitFilePath);

        unit.WriteIfChanged();
        
        foreach (var step in new[]
                 {
                     await unit.DaemonReloadAsync(ct),
                     await unit.EnableAsync(ct),
                     await unit.RestartAsync(ct),
                 })
        {
            if (!step.Ok)
            {
                Console.Error.WriteLine($"systemctl failed: {step.StdErr.Trim()}");
                return ExitCodes.RuntimeFailure;
            }
        }

        if (!await PollAsync(() => HealthOkAsync(ct), TimeSpan.FromSeconds(10)))
        {
            Console.Error.WriteLine(
                "daemon did not become healthy within 10s — diagnose with: journalctl -u boson");
            return ExitCodes.RuntimeFailure;
        }

        Console.WriteLine("✓ daemon running (healthy on 127.0.0.1:9000)");

        // Step 5 — platform rows. The daemon just migrated; the gate proves it.
        var db = new Db(paths.DbPath);
        
        new Migrator(db).CheckCompatibility();
        
        var platformRepo = new PlatformRepository(db);
        
        platformRepo.Set(PlatformRepository.AdminHostname, adminHostname);
        platformRepo.Set(PlatformRepository.InstalledAt, DateTimeOffset.UtcNow.ToString("O"));
        platformRepo.Set(PlatformRepository.BinaryVersion, VersionInfo.Version);
        
        await DbOwnership.ChownToBosonAsync(runner, paths.DbPath);

        // Step 6 — compose stack (Caddy only), YAML over stdin.
        var stack = new StackOrchestrator(docker);
        var up = await stack.UpAsync(ct);

        if (!up.Ok)
        {
            Console.Error.WriteLine($"docker compose up failed for the platform stack: {up.StdErr.Trim()}");
            return ExitCodes.RuntimeFailure;
        }

        Console.WriteLine("✓ caddy container up");

        // Step 7 — Caddy bootstrap: full config from DB state (every project on a re-run).
        var caddyClient = new CaddyAdminClient();

        if (!await PollAsync(() => caddyClient.IsReachableAsync(ct), TimeSpan.FromSeconds(10)))
        {
            Console.Error.WriteLine(
                "caddy admin API not reachable within 10s — diagnose with: docker logs boson-caddy");

            return ExitCodes.RuntimeFailure;
        }

        var projectsRepo = new ProjectsRepository(db);
        
        using (var cfg = new CaddyConfigBuilder().Build(projectsRepo.ListActive(), adminHostname))
            await caddyClient.LoadConfigAsync(cfg, ct);
        
        await DbOwnership.ChownToBosonAsync(runner, paths.DbPath);
        
        Console.WriteLine("✓ caddy config pushed");

        // Step 8 — hand off; a 200 here proves DNS, reachability and TLS end to end.
        Console.WriteLine();
        Console.WriteLine($"Platform is up. Open:  https://{adminHostname}/_boson/health");
        Console.WriteLine("(allow ~90s for the first Let's Encrypt issuance; diagnose with docker logs boson-caddy and journalctl -u boson)");

        return ExitCodes.Success;
    }

    private static async Task<bool> HealthOkAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var response = await http.GetAsync($"http://127.0.0.1:{CaddyConfigBuilder.DaemonPort}/_boson/health", ct);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> PollAsync(Func<Task<bool>> check, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await check()) return true;
            await Task.Delay(500);
        }
        
        return false;
    }
}
