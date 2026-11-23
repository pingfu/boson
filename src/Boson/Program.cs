using System.CommandLine;
using Boson.Commands;

namespace Boson;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("boson — an opinionated single-host docker PaaS driven by GitHub Apps");

        root.Subcommands.Add(InitCommand.Create());
        root.Subcommands.Add(AddCommand.Create());
        root.Subcommands.Add(DeployCommand.Create());
        root.Subcommands.Add(StatusCommand.Create());
        root.Subcommands.Add(RemoveCommand.Create());
        root.Subcommands.Add(UninstallCommand.Create());
        root.Subcommands.Add(ServeCommand.Create());

        // An exception a command lets escape is System.CommandLine's to
        // report: it prints "Unhandled exception:" with the stack trace and
        // returns 1, before anything here could catch it.
        return await root.Parse(args).InvokeAsync();
    }
}
