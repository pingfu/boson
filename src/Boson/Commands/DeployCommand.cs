using System.CommandLine;
using System.Text;
using Boson.Deploy;
using Boson.Serve;
using Boson.Storage;
using Boson.Util;

namespace Boson.Commands;

public static class DeployCommand
{
    public static Command Create()
    {
        var repoArg = new Argument<string>("org/name") { Description = "Project to deploy" };

        var branchOpt = new Option<string?>("--branch")
        {
            Description = "One branch to deploy (default: every branch this project deploys)",
        };

        var cmd = new Command("deploy", "Deploy the branch tip now (the daemon executes; this command streams the log)");

        cmd.Arguments.Add(repoArg);
        cmd.Options.Add(branchOpt);
        cmd.SetAction((parseResult, ct) =>
            RunAsync(parseResult.GetValue(repoArg)!, parseResult.GetValue(branchOpt), ct));

        return cmd;
    }

    public static async Task<int> RunAsync(string repoInput, string? branch, CancellationToken ct)
    {
        if (!RepoName.TryCanonicalise(repoInput, out var repo))
        {
            Console.Error.WriteLine($"invalid repo (expected org/name): {repoInput}");
            return ExitCodes.UserError;
        }

        var paths = new BosonPaths();
        var db = new Db(paths.DbPath);
        
        new Migrator(db).CheckCompatibility();
        
        var deploys = new DeploysRepository(db);

        using var rpc = new RpcClient(paths.SocketPath);
        
        RpcResult<DeployStartResponse> start;

        // What declaring left for the operator to do. Printed when it ends and
        // again under the deploy's outcome, because a build log scrolls the
        // first printing away and an unmoved webhook address means pushes
        // stop deploying without another word.
        string[] warnings = [];

        try
        {
            start = await rpc.DeployAsync(repo, branch, ct);

            // A project `boson add` saved and could not finish has no
            // deployments. The daemon makes them in the background and this
            // follows it, for as long as the clone takes, then asks again.
            while (Refusal(start) is null && start.Body!.Declaring)
            {
                try
                {
                    var declared = await AwaitDeclarationAsync(
                        repo, token => rpc.DeclareStatusAsync(repo, token),
                        Console.Out, Console.Error, TimeSpan.FromSeconds(1), ct);

                    if (declared.ExitCode is { } code) return code;

                    warnings = declared.Warnings;
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine();
                    Console.WriteLine(
                        $"stopped watching; the daemon finishes on its own, and boson deploy {repo} then deploys it");

                    return ExitCodes.Success;
                }

                start = await rpc.DeployAsync(repo, branch, ct);
            }
        }
        catch (DaemonUnreachableException e)
        {
            Console.Error.WriteLine(e.Message);

            return ExitCodes.RuntimeFailure;
        }

        if (Refusal(start) is { } refusal)
        {
            Console.Error.WriteLine(refusal.Message);
            PrintWarnings(Console.Error, warnings);
            return refusal.ExitCode;
        }

        var deployId = start.Body!.DeployId;
        var logPath = paths.DeployLogPath(deployId);

        Console.WriteLine($"deploy #{deployId} started; streaming {logPath}");
        Console.WriteLine();

        // Observe through the artifacts that already exist: tail the log file,
        // poll the deploys row.
        long offset = 0;

        try
        {
            await DbOwnership.ChownToBosonAsync(new ProcessRunner(), paths.DbPath);

            while (true)
            {
                offset = TailTo(Console.Out, logPath, offset);

                var row = deploys.Get(deployId);
                
                if (row?.FinishedAt is not null)
                {
                    offset = TailTo(Console.Out, logPath, offset);
                    
                    Console.WriteLine();
                    
                    if (row.Status == DeployStatus.Succeeded)
                    {
                        Console.WriteLine($"✓ deploy #{deployId} succeeded ({row.CommitSha ?? "?"})");
                        PrintWarnings(Console.Error, warnings);
                        return ExitCodes.Success;
                    }

                    Console.Error.WriteLine($"✗ deploy #{deployId} failed: {row.Error}");
                    Console.Error.WriteLine($"  full log: {logPath}");
                    PrintWarnings(Console.Error, warnings);

                    return ExitCodes.RuntimeFailure;
                }

                await Task.Delay(300, ct);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine($"stopped watching — the daemon finishes the deploy on its own; see boson status and {logPath}");
            PrintWarnings(Console.Error, warnings);

            return ExitCodes.Success;
        }
    }

    /// <summary>
    /// Follows a declaration the daemon is running until it ends, with no
    /// limit on how long that takes: each poll is its own short request, so
    /// the clone is never inside one. Returns the warnings to carry forward,
    /// or the exit code when there is nothing to deploy.
    /// </summary>
    internal static async Task<(int? ExitCode, string[] Warnings)> AwaitDeclarationAsync(
        string repo,
        Func<CancellationToken, Task<RpcResult<DeclareStatusResponse>>> poll,
        TextWriter output, TextWriter error, TimeSpan interval, CancellationToken ct)
    {
        output.WriteLine($"{repo} has no deployments yet; fetching it and reading {BosonFile.FileName}…");

        while (true)
        {
            var status = await poll(ct);

            if (status.Body is null)
            {
                error.WriteLine($"the daemon restarted while declaring {repo}; re-run boson deploy {repo}");

                return (ExitCodes.RuntimeFailure, []);
            }

            switch (status.Body.Phase)
            {
                case "done":
                    PrintWarnings(error, status.Body.Warnings);
                    output.WriteLine($"✓ {repo} declared");

                    return (null, status.Body.Warnings);
                case "failed":
                    error.WriteLine(status.Body.Error);
                    PrintWarnings(error, status.Body.Warnings);

                    return (ExitCodes.RuntimeFailure, status.Body.Warnings);
            }

            await Task.Delay(interval, ct);
        }
    }

    private static void PrintWarnings(TextWriter writer, string[] warnings)
    {
        foreach (var warning in warnings)
            writer.WriteLine($"⚠ {warning}");
    }

    /// <summary>
    /// What to print and exit with when the daemon did not start a deploy, or
    /// null when it did. The message is the daemon's own: a 404 is an unknown
    /// project or a branch with no deployment, a 409 is a deploy already
    /// running or a project that needs `--branch`, and a 422 is a deploy that
    /// ended before it had a row to follow. A message chosen here from the
    /// status would name the wrong one.
    /// </summary>
    internal static (string Message, int ExitCode)? Refusal(RpcResult<DeployStartResponse> start)
    {
        var status = (int)start.Status;

        if (status == 202 && start.Body is not null) return null;

        return (start.Error ?? $"deploy request failed ({status})", status switch
        {
            409 => ExitCodes.LockContention,
            404 => ExitCodes.UserError,
            _ => ExitCodes.RuntimeFailure,
        });
    }

    private static long TailTo(TextWriter writer, string logPath, long offset)
    {
        if (!File.Exists(logPath)) return offset;

        // The daemon holds this file open and is writing to it as we read, so
        // the share flags have to admit a concurrent writer; the default would
        // fail the open outright.
        using var stream = new FileStream(
            logPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        
        if (stream.Length <= offset) return offset;
        
        stream.Seek(offset, SeekOrigin.Begin);
        
        using var reader = new StreamReader(stream, Encoding.UTF8);
        
        writer.Write(reader.ReadToEnd());
        
        return stream.Length;
    }
}
