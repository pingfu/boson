using Microsoft.Data.Sqlite;

namespace Boson.Storage;

/// <summary>
/// Connection factory. Every connection gets WAL, foreign keys and the busy
/// timeout; the daemon and CLI processes write concurrently.
/// </summary>
public sealed class Db(string dbPath)
{
    public string DbPath { get; } = dbPath;

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        conn.Open();

        using var cmd = conn.CreateCommand();

        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
            
        cmd.ExecuteNonQuery();

        return conn;
    }

    public bool Exists() => File.Exists(DbPath);
}
