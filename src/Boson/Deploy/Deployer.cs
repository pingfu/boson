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

    /// <summary>
    /// It ended before any deploy row existed, for a branch with no deployment
    /// to record the failure against. The reason is all there is.
    /// </summary>
    public sealed record Stopped(string Error) : DeployResult;
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
    IPlatformRepository platform,
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

    // Set when the database holds a name Caddy was not told about and the
    // change could not be put back. The next preparation, for any branch,
    // synchronises before it builds.
    private volatile bool _routesStale;

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
                    // daemon log is the only record there can be. A caller
                    // that is waiting gets the reason too: with no deploy id
                    // to follow, it has nothing else to show.
                    logger.LogWarning("{Repo}#{Branch}: {Error}", repo, branch, failure.Error);

                    return passes == 1
                        ? new DeployResult.Stopped(failure.Error)
                        : new DeployResult.Completed(lastId, false, passes);
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

        BosonFile? file;
        string? problem;

        // Everything from here to the end of preparation that can go wrong is
        // turned into a failed deploy with its reason. Thrown instead, it
        // leaves the task crashed: no deploy row, no log, nothing in `boson
        // status`, and a bare 500 for whoever ran `boson deploy`.
        //
        // A timeout counts: HttpClient reports one as a cancellation, and
        // nothing here was cancelled unless the caller's own token says so.
        try
        {
            file = BosonFile.Find(checkoutDir, out problem);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return Stop(existing, $"{BosonFile.FileName} could not be read: {e.Message}", log);
        }

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

        // The whole file first, so a name any entry spells out is refused on
        // whichever branch deploys next. What a pattern generates for this
        // branch is the one name that cannot be known before the branch is.
        var control = platform.Get(PlatformRepository.AdminHostname);

        if ((file.ControlHostnameProblem(control) ?? entry.ControlHostnameProblem(branch, control)) is { } reserved)
            return Stop(existing, reserved, log);

        var hostname = entry.HostnameFor(branch);
        var aliases = string.Join(',', entry.Aliases);

        // Asked before the database is, because the database can only say
        // that some constraint failed, and the operator needs to know which
        // name is taken and what has it.
        if (ClaimedElsewhere(deployments.ListActive(), existing?.Id, entry.Aliases.Append(hostname)) is { } taken)
            return Stop(existing, taken, log);

        if (existing is null)
        {
            Deployment created;

            try
            {
                var id = deployments.Insert(new Deployment
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
                });

                created = deployments.Get(id)!;
            }
            catch (Exception e) when (e is ProjectCollisionException or BosonValidationException)
            {
                // No port left, or another branch's first deploy took the port
                // or the name between the check above and the insert.
                return Stop(null, e.Message, log);
            }

            log.Add($"{branch} declared: {hostname} on 127.0.0.1:{created.Port}");

            try
            {
                await caddy.SyncAsync(ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // Withdrawn, or the next deploy would find the deployment in
                // place, see nothing to change, skip the sync and build a site
                // Caddy has never been told about.
                deployments.Archive(created.Id);

                return Stop(null, $"caddy did not take the route for {hostname}: {e.Message}", log);
            }

            _routesStale = false;
            existing = created;
        }
        else if (existing.Hostname != hostname || existing.Aliases != aliases
                 || existing.EnvSet != entry.Env || existing.ExpireAfter != entry.ExpireAfter)
        {
            var before = existing;

            // The file is the authority, so a change in it moves routing and
            // certificates with it rather than being reported as a conflict.
            try
            {
                deployments.SetDeclared(existing.Id, hostname, aliases, entry.Env, entry.ExpireAfter);
            }
            catch (ProjectCollisionException e)
            {
                return Stop(before, e.Message, log);
            }

            log.Add($"{BosonFile.FileName} changed: {hostname}");

            try
            {
                await caddy.SyncAsync(ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // Put back for the same reason a new deployment is withdrawn:
                // the next deploy has to see the change again to retry it.
                try
                {
                    deployments.SetDeclared(
                        before.Id, before.Hostname, before.Aliases, before.EnvSet, before.ExpireAfter);
                }
                catch (ProjectCollisionException)
                {
                    // Another branch took the old name while Caddy was being
                    // asked. The new values stay, with nothing to mark them as
                    // a change, so the retry is owed by whichever deploy
                    // prepares next.
                    _routesStale = true;
                }

                return Stop(before, $"caddy did not take the change to {hostname}: {e.Message}", log);
            }

            _routesStale = false;
            existing = deployments.Get(existing.Id)!;
        }
        else if (_routesStale)
        {
            try
            {
                await caddy.SyncAsync(ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                return Stop(existing, $"caddy did not take the routes it is owed: {e.Message}", log);
            }

            _routesStale = false;
        }

        return new Preparation.Ready(existing, file, entry, checkoutDir, sha, log);
    }

    /// <summary>
    /// Which of these names another active deployment already serves, as a
    /// sentence naming the deployment that has it, or null when all are free.
    /// </summary>
    internal static string? ClaimedElsewhere(
        IEnumerable<Deployment> active, long? self, IEnumerable<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var other in active.Where(d => d.Id != self))
        {
            var held = other.AliasList.Append(other.Hostname).FirstOrDefault(wanted.Contains);

            if (held is not null)
                return $"{BosonFile.FileName}: {held} is already served by {other.Repo} branch {other.Branch}";
        }

        return null;
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
                await imageRetainer.PruneAsync(
                    config.StdOut, ready.Sha, RemovableCommits(deployment, ready.Sha, ready.File.Images.Keep),
                    ready.File.Images.Keep, Log, ct);
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

    /// <summary>
    /// The commits whose images this deploy may delete: everything the
    /// deployment has ever built, a failed deploy's image included since the
    /// build finishes before `up` can fail, less the newest few that worked.
    /// This deploy's own commit leads those because its row is still running
    /// when retention happens.
    /// </summary>
    private HashSet<string> RemovableCommits(Deployment deployment, string sha, int keep)
    {
        var history = deploys.ListCommits(deployment.Id);

        var kept = new[] { sha }
            .Concat(history.Where(c => c.Status == DeployStatus.Succeeded).Select(c => c.Sha))
            .Distinct()
            .Take(keep);

        return ComposeImages.Removable(history.Select(c => c.Sha), kept, KeptByOthers(deployment, keep));
    }

    /// <summary>
    /// What every other active deployment keeps, each by the `images.keep` in
    /// its own checkout: a branch keeping five must not lose two of them to a
    /// branch that keeps three. A checkout that cannot say falls back to the
    /// number the caller is working with. A commit another deployment is
    /// deploying at this moment is kept too: its image exists before its
    /// container does, and nothing else would stop the removal.
    /// </summary>
    private IEnumerable<IEnumerable<string>> KeptByOthers(Deployment deployment, int fallbackKeep) =>
        deployments.ListActive()
            .Where(d => d.Id != deployment.Id)
            .Select(d =>
            {
                var history = deploys.ListCommits(d.Id);

                return history
                    .Where(c => c.Status == DeployStatus.Succeeded)
                    .Select(c => c.Sha)
                    .Distinct()
                    .Take(KeepOf(d) ?? fallbackKeep)
                    .Concat(history.Where(c => c.Status == DeployStatus.Running).Select(c => c.Sha));
            })
            .ToList();

    private int? KeepOf(Deployment deployment)
    {
        try
        {
            return BosonFile.Find(paths.CheckoutDir(deployment.Repo, deployment.BranchSlug), out _)?.Images.Keep;
        }
        catch (Exception)
        {
            // Another branch's unreadable file is that branch's deploy to
            // report, not a reason for this one to fail.
            return null;
        }
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

            // Last, and never fatal: the deployment is already stopped and
            // unrouted, and a host that keeps a few images has lost nothing.
            try
            {
                await RemoveImagesAsync(deployment, composeName, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(e, "{Repo}#{Branch}: could not remove its images", deployment.Repo, deployment.Branch);
            }
        }
        finally
        {
            locks.Exit(key);
        }
    }

    /// <summary>
    /// Removes the images a torn-down deployment built. Retention runs on
    /// deploy and only over a deployment's own commits, so without this every
    /// expired preview branch leaves its last few images on the host for good.
    /// A commit another deployment still keeps is left for that deployment.
    /// </summary>
    private async Task RemoveImagesAsync(Deployment deployment, string composeName, CancellationToken ct)
    {
        var commits = deploys.ListCommits(deployment.Id).Select(c => c.Sha).Distinct().ToList();

        if (commits.Count == 0) return;

        // The image names are in the compose file, which is in the checkout
        // that teardown leaves behind.
        var checkoutDir = paths.CheckoutDir(deployment.Repo, deployment.BranchSlug);

        if (!Directory.Exists(checkoutDir))
        {
            logger.LogWarning(
                "{Repo}#{Branch}: images left in place, its checkout {Dir} is gone",
                deployment.Repo, deployment.Branch, checkoutDir);
            return;
        }

        var variables = new ComposeVariables(
            deployment.Port, paths.EnvFile(deployment.Repo, deployment.EnvSet ?? BosonPaths.DefaultEnvSet),
            commits[0], deployment.BranchSlug);

        var config = await docker.ComposeConfigAsync(composeName, checkoutDir, variables, ct);

        if (!config.Ok)
        {
            logger.LogWarning(
                "{Repo}#{Branch}: images left in place, docker compose config: {Error}",
                deployment.Repo, deployment.Branch, config.StdErr.Trim());
            return;
        }

        // A torn-down deployment keeps none of its own, and its own number is
        // only the fallback for another whose checkout cannot say.
        var fallbackKeep = KeepOf(deployment) ?? new ImagesSection().Keep;

        await imageRetainer.RemoveAsync(
            config.StdOut, commits[0],
            ComposeImages.Removable(commits, keptOwn: [], KeptByOthers(deployment, fallbackKeep)),
            line => logger.LogInformation("{Repo}#{Branch}: {Line}", deployment.Repo, deployment.Branch, line),
            ct);
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
