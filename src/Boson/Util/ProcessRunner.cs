using System.Diagnostics;
using System.Text;

namespace Boson.Util;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        string? stdin = null,
        Action<string>? onOutputLine = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        string? stdin = null,
        Action<string>? onOutputLine = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        // Added to the daemon's environment rather than replacing it: compose
        // needs PATH and HOME to find docker and its credentials.
        if (environment is not null)
            foreach (var (key, value) in environment) psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi };

        // The data-received handlers fire on threadpool threads, so the
        // builders need the locks even though only one pipe feeds each.
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) stderr.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };

        try
        {
            if (!process.Start())
                return new ProcessResult(-1, "", $"failed to start {fileName}");
        }
        catch (Exception e)
        {
            return new ProcessResult(-1, "", $"failed to start {fileName}: {e.Message}");
        }

        // Draining both pipes must start before anything is written to stdin.
        // A child that fills its stdout pipe blocks until someone reads it, and
        // if we were still writing stdin at that moment neither side could move.
        // The platform compose YAML goes in this way.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // The whole tree: `docker compose` spawns builders and per-service
            // children, and killing only the parent orphans a running build.
            // Kill throws if it already exited, which is the outcome we wanted.
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
