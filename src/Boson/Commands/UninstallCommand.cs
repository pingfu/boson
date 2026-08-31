using System.CommandLine;
using Boson.Platform;
using Boson.Storage;
using Boson.Util;
using Dapper;

namespace Boson.Commands;

public static class UninstallCommand
{
    public static Command Create()
    {
        var forceOpt = new Option<bool>("--force")
        {
            Description = "Proceed even while non-archived projects exist",
        };
        var purgeOpt = new Option<bool>("--purge")
        {
            Description = "Destroy all state too (typed confirmation required)",
        };
        var cmd = new Command("uninstall", "Walk back boson init");
        cmd.Options.Add(forceOpt);
        cmd.Options.Add(purgeOpt);
        cmd.SetAction((parseResult, ct) => RunAsync(
            parseResult.GetValue(forceOpt), parseResult.GetValue(purgeOpt), ct));
        return cmd;
    }

    public static async Task<int> RunAsync(bool force, bool purge, CancellationToken ct)
    {
        var paths = new BosonPaths();
        var runner = new ProcessRunner();
        var docker = new DockerCli(runner);

        using var platformLock = PlatformLock.TryAcquire(paths.PlatformLockPath);
        if (platformLock is null)
        {
            Console.Error.WriteLine("another boson init/uninstall is running");
            return ExitCodes.LockContention;
        }

        var db = new Db(paths.DbPath);
        var activeProjects = new List<Project>();
        if (db.Exists())
        {
            try
            {
                new Migrator(db).CheckCompatibility();
                activeProjects.AddRange(new ProjectsRepository(db).ListActive());
            }
            catch (Exception e) when (e is SchemaMismatchException)
            {
                if (!force)
                {
                    Console.Error.WriteLine($"{e.Message}");
                    Console.Error.WriteLine("cannot verify projects; use --force to uninstall anyway");
                    return ExitCodes.RuntimeFailure;
                }
            }
        }

        if (activeProjects.Count > 0 && !force)
        {
            Console.Error.WriteLine(
                $"refusing to uninstall: {activeProjects.Count} active project(s) exist:");
            foreach (var p in activeProjects)
                Console.Error.WriteLine($"  {p.Repo}  ({p.Hostname})");
            Console.Error.WriteLine("remove them first (boson remove <org/name>) or pass --force");
            return ExitCodes.UserError;
        }

        // Purge confirmation happens before anything is torn down.
        if (purge)
        {
            Console.WriteLine(
                $"--purge will destroy all boson state: {activeProjects.Count} project(s), the database, " +
                "logs, checkouts and Caddy volumes. GitHub App credentials CANNOT be re-issued by GitHub.");
            Console.Write("Type 'destroy' to confirm: ");
            if (Console.ReadLine()?.Trim() != "destroy")
            {
                Console.Error.WriteLine("aborted; nothing was changed");
                return ExitCodes.UserError;
            }

            if (db.Exists())
            {
                Console.Write("Path for a final VACUUM INTO backup of boson.db (empty to skip): ");
                var backupPath = Console.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(backupPath))
                {
                    using var conn = db.Open();
                    conn.Execute("VACUUM INTO @backupPath", new { backupPath });
                    Console.WriteLine($"✓ backup written to {backupPath}");
                }
            }
        }

        // 1 — daemon.
        if (OperatingSystem.IsLinux())
        {
            var unit = new SystemdUnit(runner, paths.UnitFilePath);
            await unit.DisableNowAsync(ct);
            unit.Remove();
            await unit.DaemonReloadAsync(ct);
            Console.WriteLine("✓ daemon stopped, unit removed");
        }

        // 2 — platform stack.
        var down = await new StackOrchestrator(docker).DownAsync(ct);
        Console.WriteLine(down.Ok ? "✓ caddy container down" : "⚠ compose down failed (continuing)");

        // 3 — user.
        if (OperatingSystem.IsLinux())
        {
            await runner.RunAsync("userdel", ["boson"], ct: ct);
            Console.WriteLine("✓ boson user removed");
        }

        if (!purge)
        {
            Console.WriteLine();
            Console.WriteLine("Kept (uninstall → init is a safe round trip):");
            Console.WriteLine($"  {paths.DbPath}  (App credentials — irreplaceable)");
            Console.WriteLine($"  {paths.DataDir}, {paths.LogDir}, {paths.SrvDir}");
            Console.WriteLine("  Caddy volumes (boson_caddy-data, boson_caddy-config)");
            return ExitCodes.Success;
        }

        // 4 — purge: state, checkouts, volumes.
        foreach (var p in activeProjects)
        {
            try
            {
                var dir = paths.ProjectDir(p.Repo);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"⚠ failed to delete {p.Repo} checkout: {e.Message}");
            }
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { paths.DataDir, paths.LogDir })
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"⚠ failed to delete {dir}: {e.Message}");
            }
        }
        await docker.VolumeRemoveAsync("boson_caddy-data", ct);
        await docker.VolumeRemoveAsync("boson_caddy-config", ct);
        Console.WriteLine("✓ state destroyed");

        if (activeProjects.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Delete each project's GitHub App yourself (boson never does):");
            foreach (var p in activeProjects)
                Console.WriteLine($"  {p.AppSettingsUrl}");
        }
        Console.WriteLine();
        Console.WriteLine("The one command boson won't run itself:  rm /usr/local/bin/boson");
        return ExitCodes.Success;
    }
}
