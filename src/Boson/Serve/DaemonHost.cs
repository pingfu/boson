using Boson.Caddy;
using Boson.Deploy;
using Boson.Github;
using Boson.Storage;
using Boson.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using Serilog;
using Serilog.Formatting.Compact;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Boson.Serve;

/// <summary>
/// The one resident boson process (spec §8): startup migration + recovery,
/// two Kestrel listeners (TCP 127.0.0.1:&lt;port&gt; fronted by Caddy, and the
/// unix-socket CLI RPC), graceful shutdown that waits for in-flight deploys.
/// </summary>
public sealed class DaemonHost(BosonPaths paths, int port)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(paths.DataDir);
        Directory.CreateDirectory(paths.LocksDir);
        Directory.CreateDirectory(paths.TmpDir);
        Directory.CreateDirectory(paths.DeployLogsDir);

        var db = new Db(paths.DbPath);
        var migrator = new Migrator(db);

        migrator.MigrateToLatest();

        var projects = new ProjectsRepository(db);
        var platform = new PlatformRepository(db);
        var deploys = new DeploysRepository(db);

        var recovered = deploys.MarkAllRunningAsFailed("daemon restart");

        // JSONL file log, rolling 10 MB × 5 (spec §17), via Serilog's file sink.
        using var serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(new CompactJsonFormatter(), paths.DaemonLogPath,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 5)
            .CreateLogger();

        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
                o.UseUtcTimestamp = true;
            });

            b.AddSerilog(serilog);
        });

        var log = loggerFactory.CreateLogger("boson");

        var runner = new ProcessRunner();
        var git = new GitCli(runner);
        var docker = new DockerCli(runner);
        var dns = new DnsResolver();
        var locks = new ProjectLocks();
        var github = new GithubClient();
        var minter = new InstallationTokenMinter(projects, github);
        var caddy = new CaddySynchroniser(new CaddyConfigBuilder(), new CaddyAdminClient(), projects, platform);
        var deployer = new Deployer(projects, deploys, minter, git, docker, locks, paths, log);
        var orchestrator = new ManifestFlowOrchestrator(projects, platform, caddy, git, minter, github, locks, dns, paths, TimeProvider.System, log);
        var publicApp = BuildPublicApp(projects, locks, deployer, orchestrator, log);
        var rpcApp = BuildRpcApp(projects, docker, caddy, deployer, orchestrator, log);

        await publicApp.StartAsync(ct);
        await rpcApp.StartAsync(ct);

        if (!OperatingSystem.IsWindows() && File.Exists(paths.SocketPath))
            File.SetUnixFileMode(paths.SocketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite);

        log.LogInformation(
            "boson {Version} listening on 127.0.0.1:{Port} and {Socket} (schema v{Schema}, {Recovered} orphaned deploys recovered)",
            VersionInfo.Version, port, paths.SocketPath, Migrator.BinarySchemaVersion, recovered);

        // systemd stops the daemon with SIGTERM. Without handling it here the
        // process ignores the signal and systemd waits out TimeoutStopSec (600s)
        // before SIGKILL, which blocks `systemctl restart` — and so `boson init`.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            stopping.Cancel();
        });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
        {
            ctx.Cancel = true;
            stopping.Cancel();
        });

        try
        {
            await Task.Delay(Timeout.Infinite, stopping.Token);
        }
        catch (OperationCanceledException)
        {
        }

        // Graceful shutdown (spec §8): stop accepting requests, let in-flight
        // deploys finish, bounded by the unit's TimeoutStopSec=600.
        log.LogInformation("shutting down; waiting for in-flight deploys");

        await publicApp.StopAsync(CancellationToken.None);
        await rpcApp.StopAsync(CancellationToken.None);
        await deployer.WaitForIdleAsync(TimeSpan.FromSeconds(590));
        await publicApp.DisposeAsync();
        await rpcApp.DisposeAsync();

        log.LogInformation("stopped");

        return ExitCodes.Success;
    }

    private WebApplication BuildPublicApp(
        IProjectsRepository projects,
        ProjectLocks locks,
        IDeployer deployer,
        ManifestFlowOrchestrator orchestrator,
        ILogger log)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenLocalhost(port);
            k.Limits.MaxRequestBodySize = WebhookEndpoint.MaxBodyBytes + 1024 * 1024;
        });

        var app = builder.Build();

        app.MapGet("/_boson/health", () =>
            Results.Json(new { status = "ok", version = VersionInfo.Version }));

        WebhookEndpoint.Map(app, projects, locks, deployer, log);

        // These three are the only record of a setup flow's progress: the
        // pending entries are in-memory, so a 404 here is otherwise invisible.
        app.MapGet("/_boson/setup-app/start", (string? state) =>
        {
            var html = orchestrator.RenderStartPage(state ?? "");
            log.LogInformation("setup-app/start: state={State} {Outcome}",
                Describe(state), html is null ? "404 no live setup for this token" : "200");
            return html is null ? Results.NotFound() : Results.Content(html, "text/html");
        });

        app.MapGet("/_boson/setup-app/callback", async (string? code, string? state) =>
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            {
                log.LogWarning("setup-app/callback: 404 missing code or state");
                return Results.NotFound();
            }

            var redirect = await orchestrator.HandleCallbackAsync(state, code);
            log.LogInformation("setup-app/callback: state={State} {Outcome}",
                Describe(state),
                redirect is null
                    ? "404 no live setup for this token (expired, already used, or the daemon restarted)"
                    : "302 to GitHub install");
            return redirect is null ? Results.NotFound() : Results.Redirect(redirect);
        });

        app.MapGet("/_boson/setup-app/installed", (HttpContext ctx) =>
        {
            var state = ctx.Request.Query["state"].ToString();
            if (!long.TryParse(ctx.Request.Query["installation_id"], out var installationId))
            {
                log.LogWarning("setup-app/installed: 404 missing installation_id");
                return Results.NotFound();
            }

            var accepted = orchestrator.HandleInstalled(state, installationId);
            log.LogInformation("setup-app/installed: state={State} installation={Installation} {Outcome}",
                Describe(state), installationId,
                accepted ? "200 finalising" : "404 no live setup for this token");
            return accepted
                ? Results.Content(EmbeddedResources.ManifestSuccessHtml, "text/html")
                : Results.NotFound();
        });

        return app;
    }

    /// <summary>Enough of a state token to correlate log lines, not enough to replay one.</summary>
    private static string Describe(string? state) =>
        string.IsNullOrEmpty(state) ? "(none)"
            : state.Length <= 8 ? state
            : state[..8] + "…";

    private WebApplication BuildRpcApp(
        IProjectsRepository projects,
        IDockerCli docker,
        ICaddySynchroniser caddy,
        IDeployer deployer,
        ManifestFlowOrchestrator orchestrator,
        ILogger log)
    {
        if (File.Exists(paths.SocketPath)) File.Delete(paths.SocketPath);

        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(paths.SocketPath));

        var app = builder.Build();

        app.MapPost("/add", async (AddRequest request) =>
        {
            try
            {
                return Results.Ok(await orchestrator.BeginAddAsync(request));
            }
            catch (BosonValidationException e)
            {
                return Results.Conflict(new ErrorResponse(e.Message));
            }
        });

        app.MapGet("/add/status/{token}", (string token) =>
        {
            var status = orchestrator.GetStatus(token);

            return status is null ? Results.NotFound() : Results.Ok(status);
        });

        app.MapPost("/deploy/{org}/{name}", async (string org, string name) =>
        {
            var repo = $"{org}/{name}".ToLowerInvariant();
            var started = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

            var deployTask = Task.Run(() =>
                deployer.DeployAsync(repo, DeployTrigger.Manual, id => started.TrySetResult(id)));
            
            _ = deployTask.ContinueWith(
                t => log.LogError(t.Exception, "{Repo}: manual deploy task crashed", repo),
                TaskContinuationOptions.OnlyOnFaulted);

            var winner = await Task.WhenAny(started.Task, deployTask);
            
            if (winner == started.Task)
                return Results.Json(new DeployStartResponse(await started.Task),
                    statusCode: StatusCodes.Status202Accepted);

            return await deployTask switch
            {
                DeployResult.Completed c => Results.Json(new DeployStartResponse(c.LastDeployId), statusCode: StatusCodes.Status202Accepted),
                DeployResult.LockHeld => Results.Conflict(new ErrorResponse("a deploy for this project is already running")),
                DeployResult.NotFound => Results.NotFound(new ErrorResponse("unknown project")),
                _ => Results.Conflict(new ErrorResponse("deploy coalesced")),
            };
        });

        app.MapPost("/remove/{org}/{name}", async (HttpContext ctx, string org, string name) =>
        {
            var repo = $"{org}/{name}".ToLowerInvariant();
            var purge = ctx.Request.Query["purge"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
            var project = projects.GetByRepo(repo);

            if (project is null) return Results.NotFound(new ErrorResponse("unknown project"));

            // The compose project name alone identifies the containers, so a
            // missing checkout doesn't block removal (spec §5).
            var down = await docker.ComposeDownAsync(RepoName.ComposeProjectName(repo));
            
            if (!down.Ok)
                log.LogWarning("{Repo}: compose down exited {Code}: {Err}",
                    repo, down.ExitCode, down.StdErr.Trim());

            if (purge)
            {
                projects.Purge(repo);
            
                try
                {
                    var dir = paths.ProjectDir(repo);

                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                }
                catch (Exception e)
                {
                    log.LogWarning(e, "{Repo}: failed to delete checkout", repo);
                }
            }
            else
            {
                projects.Archive(repo);
            }

            await caddy.SyncAsync();

            log.LogInformation("{Repo}: removed (purge={Purge})", repo, purge);
            
            return Results.Ok(new RemoveResponse(project.AppSettingsUrl, purge));
        });

        return app;
    }
}
