using Microsoft.Data.Sqlite;

namespace Boson.Storage;

/// <summary>
/// Connection factory. Every connection gets WAL, foreign keys and the busy
/// timeout; the daemon and CLI processes write concurrently.
///
/// secure_delete, because the file holds private keys: without it a key that
/// has been blanked or deleted stays readable in the page it used to occupy,
/// and so in every copy of the file.
/// </summary>
/// <param name="pooled">
/// False keeps the file closable the moment the last connection is disposed,
/// which is what a test needs to delete its database.
/// </param>
public sealed class Db(string dbPath, bool pooled = true)
{
    public string DbPath { get; } = dbPath;

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = pooled,
        }.ToString());

        conn.Open();

        using var cmd = conn.CreateCommand();

        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA secure_delete = ON;
            """;
            
        cmd.ExecuteNonQuery();

        return conn;
    }

    public bool Exists() => File.Exists(DbPath);
}
