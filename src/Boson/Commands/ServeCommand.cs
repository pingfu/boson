using System.CommandLine;
using Boson.Serve;

namespace Boson.Commands;

public static class ServeCommand
{
    public static Command Create()
    {
        var portOpt = new Option<int>("--port")
        {
            Description = "DEV-ONLY: alternative port for foreground runs on a workstation; " +
                          "production always binds 9000, which Caddy's route targets",
            DefaultValueFactory = _ => 9000,
        };

        var cmd = new Command("serve", "The daemon entry point — invoked by systemd, not by humans");
        
        cmd.Options.Add(portOpt);
        cmd.SetAction((parseResult, ct) => RunAsync(parseResult.GetValue(portOpt), ct));
        
        return cmd;
    }

    public static async Task<int> RunAsync(int port, CancellationToken ct)
    {
        var paths = new BosonPaths();
        
        return await new DaemonHost(paths, port).RunAsync(ct);
    }
}
