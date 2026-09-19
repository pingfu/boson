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

        var hostnameOpt = new Option<string>("--hostname")
        {
            Description = "Public hostname Caddy fronts for this project",
            Required = true,
        };

        var branchOpt = new Option<string>("--branch")
        {
            Description = "Branch to deploy",
            DefaultValueFactory = _ => "main",
        };

        var cmd = new Command("add", "Register a project: GitHub App manifest flow, Caddy route, initial fetch");

        cmd.Arguments.Add(repoArg);
        cmd.Options.Add(hostnameOpt);
        cmd.Options.Add(branchOpt);
        cmd.SetAction((parseResult, ct) => RunAsync(
            parseResult.GetValue(repoArg)!,
            parseResult.GetValue(hostnameOpt)!,
            parseResult.GetValue(branchOpt)!,
            ct));

        return cmd;
    }

    public static async Task<int> RunAsync(string repoInput, string hostname, string branch, CancellationToken ct)
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
            start = await rpc.AddAsync(new AddRequest(repo, hostname, branch), ct);
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
                    PrintNextSteps(paths, repo, fetchFailed: false, status.Body.Error);
                    return ExitCodes.Success;
                case "fetch_failed":
                    // Row persisted; the failure is printed and `boson deploy` retries.
                    PrintNextSteps(paths, repo, fetchFailed: true, status.Body.Error);
                    return ExitCodes.Success;
                case "failed":
                    Console.Error.WriteLine($"add failed: {status.Body.Error}");
                    Console.Error.WriteLine("nothing was persisted; re-run boson add to start over");
                    return ExitCodes.RuntimeFailure;
            }

            await Task.Delay(1000, ct);
        }
    }

    private static void PrintNextSteps(BosonPaths paths, string repo, bool fetchFailed, string? error)
    {
        Console.WriteLine();
        Console.WriteLine($"✓ {repo} added");

        if (fetchFailed)
            Console.WriteLine($"⚠ initial fetch failed: {error} — boson deploy retries it");
        
        Console.WriteLine();
        Console.WriteLine("Next steps:");
        PrintEnvStep(paths, repo, fetchFailed);
        Console.WriteLine($"  2. boson deploy {repo}");
        Console.WriteLine("The first successful deploy activates push-to-deploy.");
    }

    /// <summary>
    /// Names the environment sets the fetched repository actually asks for, so
    /// the admin creates the files this project needs rather than guessing from
    /// a generic instruction.
    /// </summary>
    private static void PrintEnvStep(BosonPaths paths, string repo, bool fetchFailed)
    {
        var envDir = paths.EnvDir(repo);

        if (fetchFailed)
        {
            Console.WriteLine($"  1. If the project's compose needs env, create {envDir}/default");
            return;
        }

        var declared = BosonFile.Find(paths.ProjectDir(repo), out var problem);

        if (problem is not null)
        {
            Console.WriteLine($"  1. Fix {problem}");
            Console.WriteLine("     Deploys fail until it parses.");
            return;
        }

        var sets = declared?.Deployments
            .Select(d => d.Env)
            .Where(set => set is not null)
            .Distinct()
            .Order()
            .ToList() ?? [];

        if (sets.Count == 0)
        {
            Console.WriteLine(
                $"  1. If the project's compose needs env, create {Path.Combine(envDir, BosonPaths.DefaultEnvSet)}");
            Console.WriteLine("     (copy the repo's template; compose reads it as $BOSON_ENV_FILE).");
            return;
        }

        Console.WriteLine($"  1. Create the environment sets {BosonFile.FileName} names:");

        foreach (var set in sets)
            Console.WriteLine($"     {Path.Combine(envDir, set!)}");

        Console.WriteLine("     (copy the repo's template; compose reads them as $BOSON_ENV_FILE).");
    }
}
