using System.CommandLine;
using Boson.Serve;

namespace Boson.Commands;

public static class RemoveCommand
{
    public static Command Create()
    {
        var repoArg = new Argument<string>("org/name") { Description = "Project to remove" };

        var purgeOpt = new Option<bool>("--purge")
        {
            Description = "Hard-delete the project row (deploy history cascades) and the checkout",
        };
        
        var cmd = new Command("remove", "Tear down a project, archive its record");
        
        cmd.Arguments.Add(repoArg);
        cmd.Options.Add(purgeOpt);
        cmd.SetAction((parseResult, ct) => RunAsync(parseResult.GetValue(repoArg)!, parseResult.GetValue(purgeOpt), ct));
        
        return cmd;
    }

    public static async Task<int> RunAsync(string repoInput, bool purge, CancellationToken ct)
    {
        if (!RepoName.TryCanonicalise(repoInput, out var repo))
        {
            Console.Error.WriteLine($"invalid repo (expected org/name): {repoInput}");
            return ExitCodes.UserError;
        }

        var paths = new BosonPaths();
        
        using var rpc = new RpcClient(paths.SocketPath);

        RpcResult<RemoveResponse> result;
        
        try
        {
            result = await rpc.RemoveAsync(repo, purge, ct);
        }
        catch (DaemonUnreachableException e)
        {
            Console.Error.WriteLine(e.Message);
            return ExitCodes.RuntimeFailure;
        }

        if (result.Body is null)
        {
            if ((int)result.Status == 404)
            {
                Console.Error.WriteLine($"unknown project: {repo}");
                return ExitCodes.UserError;
            }

            Console.Error.WriteLine(result.Error ?? $"remove failed ({(int)result.Status})");
            
            return ExitCodes.RuntimeFailure;
        }

        Console.WriteLine(purge
            ? $"✓ {repo} removed; row, deploy history and checkout deleted"
            : $"✓ {repo} removed; record archived (deploy history kept, checkout left at {paths.ProjectDir(repo)})");

        Console.WriteLine(
            $"The GitHub App is not uninstalled by boson — delete it yourself at: {result.Body.AppSettingsUrl}");
        
        return ExitCodes.Success;
    }
}
