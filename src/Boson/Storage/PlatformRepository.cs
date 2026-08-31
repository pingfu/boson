using Dapper;

namespace Boson.Storage;

public interface IPlatformRepository
{
    string? Get(string key);
    void Set(string key, string value);
}

public sealed class PlatformRepository(Db db) : IPlatformRepository
{
    public const string AdminHostname = "admin_hostname";
    /// <summary>"1" when the control hostname is private and Caddy must self-issue its certificate.</summary>
    public const string AdminTlsInternal = "admin_tls_internal";
    public const string InstalledAt = "installed_at";
    public const string BinaryVersion = "binary_version";

    public string? Get(string key)
    {
        using var conn = db.Open();
        return conn.QuerySingleOrDefault<string>(
            "SELECT value FROM platform WHERE key = @key", new { key });
    }

    public void Set(string key, string value)
    {
        using var conn = db.Open();
        conn.Execute("""
            INSERT INTO platform(key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = @value
            """, new { key, value });
    }
}
