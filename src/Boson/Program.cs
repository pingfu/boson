using System.CommandLine;
using Boson.Commands;
using Boson.Github;
using Boson.Serve;
using Boson.Storage;

namespace Boson;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("boson — an opinionated single-host docker PaaS driven by GitHub Apps");

        root.Subcommands.Add(InitCommand.Create());
        root.Subcommands.Add(AddCommand.Create());
        root.Subcommands.Add(DeployCommand.Create());
        root.Subcommands.Add(ListCommand.Create());
        root.Subcommands.Add(RemoveCommand.Create());
        root.Subcommands.Add(UninstallCommand.Create());
        root.Subcommands.Add(ServeCommand.Create());

        try
        {
            return await root.Parse(args).InvokeAsync();
        }
        catch (SchemaMismatchException e)
        {
            Console.Error.WriteLine(e.Message);
            return ExitCodes.RuntimeFailure;
        }
        catch (BosonValidationException e)
        {
            Console.Error.WriteLine(e.Message);
            return ExitCodes.UserError;
        }
        catch (ProjectCollisionException e)
        {
            Console.Error.WriteLine(e.Message);
            return ExitCodes.UserError;
        }
        catch (DaemonUnreachableException e)
        {
            Console.Error.WriteLine(e.Message);
            return ExitCodes.RuntimeFailure;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.RuntimeFailure;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("internal error — please file an issue at github.com/pingfu/boson:");
            Console.Error.WriteLine(e.ToString());

            return ExitCodes.InternalBug;
        }
    }
}
