using System.CommandLine;
using Boson.Deploy;
using Boson.Serve;
using Boson.Util;

namespace Boson.Commands;

public static class AddCommand
{
    public static Command Create()
    {
        var repoArg = new Argument<string>("org/name") { Description = "GitHub repository" };

        // Nothing else to pass: hostnames, branches and environment sets are in
        // the repository's own `_boson.yml`, which this command clones to read.
        var cmd = new Command("add", "Register a project: GitHub App manifest flow, clone, deployments from _boson.yml");

        cmd.Arguments.Add(repoArg);
        cmd.SetAction((parseResult, ct) => RunAsync(parseResult.GetValue(repoArg)!, ct));

        return cmd;
    }

    public static async Task<int> RunAsync(string repoInput, CancellationToken ct)
    {
        if (!RepoName.TryCanonicalise(repoInput, out var repo))
        {
            Console.Error.WriteLine($"invalid repo (expected org/name): {repoInput}");
            return ExitCodes.UserError;
        }

        var paths = new BosonPaths();

        using var rpc = new RpcClient(paths.SocketPath);

        RpcResult<AddStartResponse> start;
        
        try
        {
            start = await rpc.AddAsync(new AddRequest(repo), ct);
        }
        catch (DaemonUnreachableException e)
        {
            Console.Error.WriteLine(e.Message);

            return ExitCodes.RuntimeFailure;
        }

        if (start.Body is null)
        {
            Console.Error.WriteLine(start.Error ?? $"add refused ({(int)start.Status})");

            return ExitCodes.UserError;
        }

        foreach (var warning in start.Body.Warnings)
            Console.WriteLine($"⚠ {warning}");

        Console.WriteLine();
        Console.WriteLine("Open this URL to create and install the project's GitHub App:");
        Console.WriteLine($"  {start.Body.SetupUrl}");

        if (BrowserLauncher.TryOpen(start.Body.SetupUrl))
            Console.WriteLine("(opened in your browser)");
        
        Console.WriteLine();

        // The daemon owns the flow; this loop only observes it. Ctrl-C here
        // stops the display, not the flow.
        string? lastPhase = null;

        while (true)
        {
            RpcResult<AddStatusResponse> status;

            try
            {
                status = await rpc.AddStatusAsync(start.Body.Token, ct);
            }
            catch (DaemonUnreachableException e)
            {
                Console.Error.WriteLine(e.Message);
                return ExitCodes.RuntimeFailure;
            }

            if (status.Body is null)
            {
                Console.Error.WriteLine(
                    "setup expired or the daemon restarted — re-run boson add to start over " +
                    "(nothing was persisted; an orphaned GitHub App is deleted by hand)");
                return ExitCodes.RuntimeFailure;
            }

            var phase = status.Body.Phase;
            if (phase != lastPhase)
            {
                lastPhase = phase;
                var line = phase switch
                {
                    "awaiting_manifest" => "waiting for App creation on GitHub…",
                    "awaiting_install" => "App created; waiting for installation…",
                    "finalizing" => "saving project…",
                    "fetching" => "fetching checkout…",
                    _ => null,
                };

                if (line is not null) Console.WriteLine($"  {line}");
            }

            switch (phase)
            {
                case "done":
                    PrintNextSteps(paths, repo, fetchFailed: false, status.Body.Error, status.Body.Warnings, status.Body.EnvSets);
                    return ExitCodes.Success;
                case "fetch_failed":
                    // Row persisted; the failure is printed and `boson deploy` retries.
                    PrintNextSteps(paths, repo, fetchFailed: true, status.Body.Error, status.Body.Warnings, status.Body.EnvSets);
                    return ExitCodes.Success;
                case "failed":
                    Console.Error.WriteLine($"add failed: {status.Body.Error}");
                    Console.Error.WriteLine("nothing was persisted; re-run boson add to start over");
                    return ExitCodes.RuntimeFailure;
            }

            await Task.Delay(1000, ct);
        }
    }

    private static void PrintNextSteps(
        BosonPaths paths, string repo, bool fetchFailed, string? error,
        string[] warnings, string[] envSets)
    {
        Console.WriteLine();
        Console.WriteLine($"✓ {repo} added");

        // The App and its keys are saved either way: GitHub issues them once,
        // and everything reported here is fixed by a push or a retry.
        if (fetchFailed)
            Console.WriteLine($"⚠ {error}");

        foreach (var warning in warnings)
            Console.WriteLine($"⚠ {warning}");

        Console.WriteLine();
        Console.WriteLine("Next steps:");
        PrintEnvStep(paths, repo, envSets, fetchFailed);
        Console.WriteLine($"  2. boson deploy {repo}");
        Console.WriteLine("The first successful deploy of a branch activates push-to-deploy for it.");
    }

    /// <summary>
    /// Names the environment sets the cloned repository actually asks for, so
    /// the admin creates the files this project needs rather than guessing from
    /// a generic instruction. The daemon read them out of `_boson.yml`.
    /// </summary>
    private static void PrintEnvStep(BosonPaths paths, string repo, string[] envSets, bool unread)
    {
        var envDir = paths.EnvDir(repo);

        if (unread)
        {
            Console.WriteLine($"  1. Fix the above, push, then create whatever environment sets");
            Console.WriteLine($"     {BosonFile.FileName} names, under {envDir}/");
            return;
        }

        if (envSets.Length == 0)
        {
            Console.WriteLine($"  1. {BosonFile.FileName} names no environment set, so nothing to create");
            return;
        }

        Console.WriteLine($"  1. Create the environment sets {BosonFile.FileName} names:");

        foreach (var set in envSets)
            Console.WriteLine($"     {Path.Combine(envDir, set)}");

        Console.WriteLine("     (copy the repo's template; compose reads them as $BOSON_ENV_FILE).");
    }
}
