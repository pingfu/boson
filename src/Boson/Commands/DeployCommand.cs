using System.CommandLine;
using System.Text;
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
        
        try
        {
            start = await rpc.DeployAsync(repo, branch, ct);
        }
        catch (DaemonUnreachableException e)
        {
            Console.Error.WriteLine(e.Message);

            return ExitCodes.RuntimeFailure;
        }

        switch ((int)start.Status)
        {
            case 202 when start.Body is not null:
                break;
            case 409:
                Console.Error.WriteLine(
                    $"a deploy for {repo} is already running — re-run when it finishes (boson status shows it)");
                return ExitCodes.LockContention;
            case 404:
                Console.Error.WriteLine($"unknown project: {repo}");
                return ExitCodes.UserError;
            default:
                Console.Error.WriteLine(start.Error ?? $"deploy request failed ({(int)start.Status})");
                return ExitCodes.RuntimeFailure;
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
                        return ExitCodes.Success;
                    }
                    
                    Console.Error.WriteLine($"✗ deploy #{deployId} failed: {row.Error}");
                    Console.Error.WriteLine($"  full log: {logPath}");
                    
                    return ExitCodes.RuntimeFailure;
                }

                await Task.Delay(300, ct);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine($"stopped watching — the daemon finishes the deploy on its own; see boson status and {logPath}");

            return ExitCodes.Success;
        }
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
