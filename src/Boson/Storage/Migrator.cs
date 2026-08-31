using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;

namespace Boson.Storage;

/// <summary>
/// Applies embedded SQL migrations. Runs at daemon startup only (spec §8);
/// every other invocation calls <see cref="CheckCompatibility"/> and refuses
/// on mismatch.
/// </summary>
public sealed partial class Migrator(Db db)
{
    [GeneratedRegex(@"migrations\.(\d{4})_.+\.sql$")]
    private static partial Regex MigrationName();

    public static int BinarySchemaVersion => LoadMigrations().Keys.Max();

    private static SortedDictionary<int, string> LoadMigrations()
    {
        var asm = Assembly.GetExecutingAssembly();
        var result = new SortedDictionary<int, string>();

        foreach (var name in asm.GetManifestResourceNames())
        {
            var m = MigrationName().Match(name);

            if (!m.Success) continue;
            
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            
            result[int.Parse(m.Groups[1].Value)] = reader.ReadToEnd();
        }

        if (result.Count == 0)
            throw new InvalidOperationException("no embedded migrations found");
        
        return result;
    }

    public int CurrentVersion()
    {
        if (!db.Exists()) return 0;

        using var conn = db.Open();
        
        var hasTable = conn.ExecuteScalar<long>(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='schema_version'");
        
        if (hasTable == 0) return 0;
        
        return conn.ExecuteScalar<int?>("SELECT max(version) FROM schema_version") ?? 0;
    }

    /// <summary>Daemon startup only (spec §8).</summary>
    public void MigrateToLatest()
    {
        var current = CurrentVersion();

        var target = BinarySchemaVersion;
        
        if (current > target)
            throw new SchemaMismatchException(current, target,
                $"database schema (v{current}) is newer than this binary (v{target}); upgrade the binary");

        var migrations = LoadMigrations();
        
        using var conn = db.Open();
        
        foreach (var (version, sql) in migrations)
        {
            if (version <= current) continue;
        
            using var tx = conn.BeginTransaction();
        
            conn.Execute(sql, transaction: tx);
            conn.Execute(
                "INSERT INTO schema_version(version) VALUES (@version)",
                new { version }, tx);
        
            tx.Commit();
        }
    }

    /// <summary>Every non-daemon invocation that touches the DB (spec §5, §8).</summary>
    public void CheckCompatibility()
    {
        var current = CurrentVersion();
        var target = BinarySchemaVersion;

        if (current < target)
            throw new SchemaMismatchException(current, target,
                $"database schema (v{current}) is behind this binary (v{target}); run: systemctl restart boson");
        
        if (current > target)
            throw new SchemaMismatchException(current, target,
                $"database schema (v{current}) is newer than this binary (v{target}); upgrade the binary");
    }
}

public sealed class SchemaMismatchException(int dbVersion, int binaryVersion, string message)
    : Exception(message)
{
    public int DbVersion { get; } = dbVersion;
    public int BinaryVersion { get; } = binaryVersion;
}
