using Boson.Caddy;
using Boson.Github;
using Boson.Platform;
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

    /// <summary>The branch exists and `_boson.yml` says nothing about it.</summary>
    public sealed record NotDeclared : DeployResult;
}

public interface IDeployer
{
    /// <summary>
    /// Runs the full deploy loop for one branch, including the deploy_pending
    /// drain. Every deploy means "deploy the branch tip, now": no target commit
    /// is passed, so a push that lands while an earlier one is being deployed is
    /// satisfied by the newer tip rather than queueing a deploy per commit.
    /// <paramref name="onStarted"/> fires once with the first pass's deploy id,
    /// before any deploy work, so the CLI can start tailing the log.
    /// </summary>
    Task<DeployResult> DeployAsync(
        string repo, string branch, DeployTrigger trigger,
        Action<long>? onStarted = null, CancellationToken ct = default);

    /// <summary>
    /// Stops a deployment's containers and releases its hostname and port. The
    /// checkout stays: it is cheap, and a push brings the deployment back
    /// rather than starting from nothing.
    /// </summary>
    Task TearDownAsync(Deployment deployment, CancellationToken ct = default);

    /// <summary>Graceful shutdown support: wait for in-flight deploys.</summary>
    Task WaitForIdleAsync(TimeSpan timeout);
}

public sealed class Deployer(
    IProjectsRepository projects,
    IDeploymentsRepository deployments,
    IDeploysRepository deploys,
    IInstallationTokenMinter minter,
    IGitCli git,
    IDockerCli docker,
    IImageRetainer imageRetainer,
    ICaddySynchroniser caddy,
    DeploymentLocks locks,
    BosonPaths paths,
    ILogger logger) : IDeployer
{
    // The drain is bounded so a push flood can't hold the deployment lock
    // indefinitely; a still-set flag is surfaced by `boson status`.
    internal const int MaxPasses = 3;

    private int _active;

    public async Task<DeployResult> DeployAsync(
        string repo, string branch, DeployTrigger trigger,
        Action<long>? onStarted = null, CancellationToken ct = default)
    {
        if (projects.GetByRepo(repo) is null) return new DeployResult.NotFound();

        // Per branch, not per repository: two branches of one repository are
        // separate containers with separate ports and nothing to serialise.
        var key = LockKey(repo, branch);

        // Non-blocking: a webhook that finds the lock held records its intent
        // and returns, so GitHub gets its 202 inside the 10-second budget
        // instead of waiting out a deploy that takes minutes.
        if (!locks.TryEnter(key))
        {
            if (trigger != DeployTrigger.Webhook) return new DeployResult.LockHeld();

            if (deployments.GetByBranch(repo, branch) is { } held)
            {
                deployments.SetDeployPending(held.Id, true);
                logger.LogInformation(
                    "{Repo}#{Branch}: deploy in flight; push coalesced into deploy_pending", repo, branch);
            }

            return new DeployResult.Coalesced();
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

                var prepared = await PrepareAsync(repo, branch, ct);

                if (prepared is Preparation.Undeclared)
                    return passes == 1 ? new DeployResult.NotDeclared() : new DeployResult.Completed(lastId, lastSucceeded, passes);

                if (prepared is Preparation.Failed failure)
                {
                    // No deployment row exists to hang the failure on, so the
                    // daemon log is the only record there can be.
                    logger.LogWarning("{Repo}#{Branch}: {Error}", repo, branch, failure.Error);
                    return new DeployResult.Completed(lastId, false, passes);
                }

                if (prepared is Preparation.Broken broken)
                {
                    // A deployment exists, so the failure belongs on its
                    // history where `boson status` will show it.
                    var brokenId = deploys.Insert(broken.Deployment.Id, currentTrigger, paths.DeployLogPath);

                    if (passes == 1) onStarted?.Invoke(brokenId);

                    RecordFailure(broken, brokenId, currentTrigger);

                    return new DeployResult.Completed(brokenId, false, passes);
                }

                var ready = (Preparation.Ready)prepared;
                var deployment = ready.Deployment;

                var deployId = deploys.Insert(deployment.Id, currentTrigger, paths.DeployLogPath);

                lastId = deployId;

                if (passes == 1) onStarted?.Invoke(deployId);

                lastSucceeded = await RunPassAsync(ready, deployId, currentTrigger, ct);

                // Activation is purely this DB flag, and only a success flips
                // it: a deployment whose first deploy failed stays inert, so a
                // half-configured repo can't auto-deploy on the next push.
                if (lastSucceeded && !deployment.WebhookActive)
                {
                    deployments.MarkWebhookActive(deployment.Id);

                    logger.LogInformation(
                        "{Repo}#{Branch}: first successful deploy; push-to-deploy activated", repo, branch);
                }

                // Drain while still holding the lock, so a push that arrived
                // mid-deploy lands without a second caller racing in. Draining
                // happens whether this pass succeeded or failed: a newer commit
                // is often the fix for a broken one.
                var fresh = deployments.GetByBranch(repo, branch);

                if (fresh is not { DeployPending: true }) break;

                if (passes >= MaxPasses)
                {
                    logger.LogWarning(
                        "{Repo}#{Branch}: deploy_pending still set after {Passes} passes; leaving it for `boson status` to surface",
                        repo, branch, passes);
                    break;
                }

                deployments.SetDeployPending(fresh.Id, false);

                currentTrigger = DeployTrigger.Webhook;
            }

            return new DeployResult.Completed(lastId, lastSucceeded, passes);
        }
        finally
        {
            locks.Exit(key);
            Interlocked.Decrement(ref _active);
        }
    }

    internal static string LockKey(string repo, string branch) => $"{repo}#{branch}";

    private abstract record Preparation
    {
        /// <summary>The branch deploys, and everything needed to deploy it is in hand.</summary>
        public sealed record Ready(
            Deployment Deployment, BosonFile File, DeploymentEntry Entry,
            string CheckoutDir, string Sha, IReadOnlyList<string> Log) : Preparation;

        /// <summary>Nothing declares this branch, and boson has never deployed it.</summary>
        public sealed record Undeclared : Preparation;

        /// <summary>It went wrong before any deployment existed to record it against.</summary>
        public sealed record Failed(string Error) : Preparation;

        /// <summary>It went wrong, and the deployment it went wrong for is on record.</summary>
        public sealed record Broken(
            Deployment Deployment, string Error, IReadOnlyList<string> Log) : Preparation;
    }

    /// <summary>
    /// Where a preparation failure lands depends on whether there is a
    /// deployment to hang it on: one that exists gets the failure on its
    /// history, and one that never existed has only the daemon log.
    /// </summary>
    private static Preparation Stop(Deployment? existing, string error, List<string> log) =>
        existing is null
            ? new Preparation.Failed(error)
            : new Preparation.Broken(existing, error, [.. log, $"error: {error}"]);

    /// <summary>
    /// Fetches the branch and reads what it declares, before any row exists.
    /// A branch boson has never seen is discovered here: the file is the only
    /// thing that can say whether it deploys at all, and the file only exists
    /// in the checkout.
    /// </summary>
    private async Task<Preparation> PrepareAsync(string repo, string branch, CancellationToken ct)
    {
        var existing = deployments.GetByBranch(repo, branch);
        var slug = existing?.BranchSlug ?? BranchSlug.From(branch);
        var checkoutDir = paths.CheckoutDir(repo, slug);
        var log = new List<string>();

        void Log(string line) => log.Add(line);

        string sha;

        try
        {
            var token = await minter.MintAsync(repo, ct);

            sha = await git.FetchAndResetAsync(checkoutDir, repo, branch, token.Value, Log, ct);
        }
        catch (Exception e)
        {
            return Stop(existing, $"fetch: {e.Message}", log);
        }

        var file = BosonFile.Find(checkoutDir, out var problem);

        if (file is null)
            return Stop(existing, problem ?? $"no {BosonFile.FileName} at the root of {repo}", log);

        var entry = file.Match(branch);

        if (entry is null)
        {
            // Nothing declares this branch. A checkout only this deploy created
            // is removed, so a push to a scratch branch leaves no trace.
            if (existing is null)
            {
                TryDelete(checkoutDir);
                return new Preparation.Undeclared();
            }

            return Stop(existing, $"{BosonFile.FileName} no longer declares {branch}", log);
        }

        var hostname = entry.HostnameFor(branch);
        var aliases = string.Join(',', entry.Aliases);

        if (existing is null)
        {
            var deployment = new Deployment
            {
                ProjectId = projects.GetByRepo(repo)!.Id,
                Repo = repo,
                Branch = branch,
                BranchSlug = slug,
                Hostname = hostname,
                Aliases = aliases,
                Port = PortAllocator.Allocate(
                    deployments.ListActive().Select(d => d.Port), PortAllocator.IsFreeOnHost),
                EnvSet = entry.Env,
                ExpireAfter = entry.ExpireAfter,
            };

            var id = deployments.Insert(deployment);

            log.Add($"{branch} declared: {hostname} on 127.0.0.1:{deployment.Port}");

            existing = deployments.Get(id)!;

            await caddy.SyncAsync(ct);
        }
        else if (existing.Hostname != hostname || existing.Aliases != aliases
                 || existing.EnvSet != entry.Env || existing.ExpireAfter != entry.ExpireAfter)
        {
            // The file is the authority, so a change in it moves routing and
            // certificates with it rather than being reported as a conflict.
            deployments.SetDeclared(existing.Id, hostname, aliases, entry.Env, entry.ExpireAfter);

            log.Add($"{BosonFile.FileName} changed: {hostname}");

            existing = deployments.Get(existing.Id)!;

            await caddy.SyncAsync(ct);
        }

        return new Preparation.Ready(existing, file, entry, checkoutDir, sha, log);
    }

    /// <summary>
    /// Writes the deploy log for something that failed before it could start,
    /// so the reason is where every other deploy's reason is.
    /// </summary>
    private void RecordFailure(Preparation.Broken broken, long deployId, DeployTrigger trigger)
    {
        var logPath = paths.DeployLogPath(deployId);

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        File.WriteAllLines(logPath,
        [
            $"deploy #{deployId}: {broken.Deployment.Repo} branch {broken.Deployment.Branch} ({trigger.AsDbValue()})",
            .. broken.Log,
            $"deploy failed: {broken.Error}",
        ]);

        deploys.Finish(deployId, succeeded: false, broken.Error);
    }

    private async Task<bool> RunPassAsync(
        Preparation.Ready ready, long deployId, DeployTrigger trigger, CancellationToken ct)
    {
        var deployment = ready.Deployment;
        var logPath = paths.DeployLogPath(deployId);

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        await using var logFile = new StreamWriter(new FileStream(
            logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };

        var logGate = new object();

        void Log(string line) { lock (logGate) logFile.WriteLine(line); }

        Log($"deploy #{deployId}: {deployment.Repo} branch {deployment.Branch} ({trigger.AsDbValue()})");

        // Everything the fetch and the file said, before this row existed.
        foreach (var line in ready.Log) Log(line);

        var succeeded = false;

        string? error = null;

        try
        {
            deploys.SetCommitSha(deployId, ready.Sha);

            Log($"HEAD {ready.Sha}");

            var composeName = RepoName.ComposeProjectName(deployment.Repo, deployment.BranchSlug);

            var envSet = deployment.EnvSet ?? BosonPaths.DefaultEnvSet;
            var envFile = paths.EnvFile(deployment.Repo, envSet);

            Log($"env set {envSet} ({envFile})");

            // Only when the file asks for one by name: a deployment that
            // declares no set needs no file, and compose never reads the path.
            if (deployment.EnvSet is not null && !File.Exists(envFile))
                throw new DeployStepException(
                    $"{BosonFile.FileName} names env set {envSet} for {deployment.Branch}, " +
                    $"and {envFile} does not exist");

            var variables = new ComposeVariables(
                deployment.Port, envFile, ready.Sha, deployment.BranchSlug);

            // Before the build, not after: a compose file publishing the wrong
            // port builds perfectly and then serves nothing, and the 502 that
            // follows says nothing about why.
            var config = await docker.ComposeConfigAsync(composeName, ready.CheckoutDir, variables, ct);

            if (!config.Ok)
                throw new DeployStepException($"docker compose config: {config.StdErr.Trim()}");

            if (ComposePorts.Problem(config.StdOut, deployment.Port) is { } problem)
                throw new DeployStepException(problem);

            var result = await docker.ComposeUpBuildAsync(
                composeName, ready.CheckoutDir, variables, Log, ct);

            succeeded = result.Ok;

            if (!succeeded) error = $"docker compose up exited {result.ExitCode}";

            if (succeeded)
                await imageRetainer.PruneAsync(config.StdOut, ready.Sha, ready.File.Images.Keep, Log, ct);
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

    public async Task TearDownAsync(Deployment deployment, CancellationToken ct = default)
    {
        var key = LockKey(deployment.Repo, deployment.Branch);

        if (!locks.TryEnter(key))
        {
            logger.LogInformation(
                "{Repo}#{Branch}: teardown skipped because a deploy is in flight",
                deployment.Repo, deployment.Branch);
            return;
        }

        try
        {
            var composeName = RepoName.ComposeProjectName(deployment.Repo, deployment.BranchSlug);

            await docker.ComposeDownAsync(composeName, ct);

            deployments.Archive(deployment.Id);

            await caddy.SyncAsync(ct);

            logger.LogInformation("{Repo}#{Branch}: torn down", deployment.Repo, deployment.Branch);
        }
        finally
        {
            locks.Exit(key);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async Task WaitForIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (Volatile.Read(ref _active) > 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(250);
    }
}

public sealed class DeployStepException(string message) : Exception(message);
