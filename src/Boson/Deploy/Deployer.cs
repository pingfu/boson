using Boson.Github;
using Boson.Storage;
using Boson.Util;
using Microsoft.Extensions.Logging;

namespace Boson.Deploy;

public abstract record DeployResult
{
    public sealed record Completed(long LastDeployId, bool Succeeded, int Passes) : DeployResult;
    public sealed record Coalesced : DeployResult;
    public sealed record LockHeld : DeployResult;
    public sealed record NotFound : DeployResult;
}

public interface IDeployer
{
    /// <summary>
    /// Runs the full deploy loop, including the deploy_pending drain.
    /// Every deploy means "deploy the branch tip, now": no target commit is
    /// passed, so a push that lands while an earlier one is being deployed is
    /// satisfied by the newer tip rather than queueing a deploy per commit.
    /// <paramref name="onStarted"/> fires once with the first pass's deploy id,
    /// before any deploy work, so the CLI can start tailing the log.
    /// </summary>
    Task<DeployResult> DeployAsync(
        string repo, DeployTrigger trigger,
        Action<long>? onStarted = null, CancellationToken ct = default);

    /// <summary>Graceful shutdown support: wait for in-flight deploys.</summary>
    Task WaitForIdleAsync(TimeSpan timeout);
}

public sealed class Deployer(
    IProjectsRepository projects,
    IDeploysRepository deploys,
    IInstallationTokenMinter minter,
    IGitCli git,
    IDockerCli docker,
    ProjectLocks locks,
    BosonPaths paths,
    ILogger logger) : IDeployer
{
    // The drain is bounded so a push flood can't hold the project lock
    // indefinitely; a still-set flag is surfaced by `boson list`.
    internal const int MaxPasses = 3;

    private int _active;

    public async Task<DeployResult> DeployAsync(
        string repo, DeployTrigger trigger,
        Action<long>? onStarted = null, CancellationToken ct = default)
    {
        if (projects.GetByRepo(repo) is null)
            return new DeployResult.NotFound();

        // Non-blocking: a webhook that finds the lock held records its intent
        // and returns, so GitHub gets its 202 inside the 10-second budget
        // instead of waiting out a deploy that takes minutes.
        if (!locks.TryEnter(repo))
        {
            if (trigger == DeployTrigger.Webhook)
            {
                projects.SetDeployPending(repo, true);
                logger.LogInformation("{Repo}: deploy in flight; push coalesced into deploy_pending", repo);
                return new DeployResult.Coalesced();
            }

            return new DeployResult.LockHeld();
        }

        Interlocked.Increment(ref _active);

        try
        {
            var currentTrigger = trigger;
            var passes = 0;
            long lastId = 0;
            var lastSucceeded = false;

            while (true)
            {
                passes++;

                var project = projects.GetByRepo(repo);
                
                if (project is null) break; // removed mid-loop

                var deployId = deploys.Insert(project.Id, currentTrigger, paths.DeployLogPath);
                
                lastId = deployId;
                
                if (passes == 1) onStarted?.Invoke(deployId);

                lastSucceeded = await RunPassAsync(project, deployId, currentTrigger, ct);

                // Activation is purely this DB flag, and only a success flips
                // it: a project whose first deploy failed stays inert, so a
                // half-configured repo can't auto-deploy on the next push.
                if (lastSucceeded && !project.WebhookActive)
                {
                    projects.MarkWebhookActive(repo);

                    logger.LogInformation("{Repo}: first successful deploy; push-to-deploy activated", repo);
                }

                // Drain while still holding the lock, so a push that arrived
                // mid-deploy lands without a second caller racing in. Draining
                // happens whether this pass succeeded or failed: a newer commit
                // is often the fix for a broken one.
                var fresh = projects.GetByRepo(repo);
                if (fresh is not { DeployPending: true }) break;

                if (passes >= MaxPasses)
                {
                    logger.LogWarning(
                        "{Repo}: deploy_pending still set after {Passes} passes; leaving it for `boson list` to surface",
                        repo, passes);
                    break;
                }

                projects.SetDeployPending(repo, false);
                
                currentTrigger = DeployTrigger.Webhook;
            }

            return new DeployResult.Completed(lastId, lastSucceeded, passes);
        }
        finally
        {
            locks.Exit(repo);
            Interlocked.Decrement(ref _active);
        }
    }

    private async Task<bool> RunPassAsync(Project project, long deployId, DeployTrigger trigger, CancellationToken ct)
    {
        var logPath = paths.DeployLogPath(deployId);

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        
        await using var logFile = new StreamWriter(new FileStream(
            logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };
        
        var logGate = new object();
        
        void Log(string line) { lock (logGate) logFile.WriteLine(line); }

        var succeeded = false;
        
        string? error = null;
        
        try
        {
            Log($"deploy #{deployId}: {project.Repo} branch {project.Branch} ({trigger.AsDbValue()})");

            InstallationToken token;
        
            try
            {
                token = await minter.MintAsync(project.Repo, ct);
            }
            catch (Exception e)
            {
                throw new DeployStepException($"token mint: {e.Message}");
            }

            var projectDir = paths.ProjectDir(project.Repo);
            var sha = await git.FetchAndResetAsync(projectDir, project.Repo, project.Branch, token.Value, Log, ct);

            deploys.SetCommitSha(deployId, sha);
            
            Log($"HEAD {sha}");

            var composeName = RepoName.ComposeProjectName(project.Repo);
            var result = await docker.ComposeUpBuildAsync(composeName, projectDir, Log, ct);
            
            succeeded = result.Ok;
            
            if (!succeeded) error = $"docker compose up exited {result.ExitCode}";
        }
        catch (Exception e)
        {
            error = e.Message;
            Log($"error: {e.Message}");
        }

        deploys.Finish(deployId, succeeded, error);
        
        Log(succeeded ? "deploy succeeded" : $"deploy failed: {error}");

        return succeeded;
    }

    public async Task WaitForIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        
        while (Volatile.Read(ref _active) > 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(250);
    }
}

public sealed class DeployStepException(string message) : Exception(message);
