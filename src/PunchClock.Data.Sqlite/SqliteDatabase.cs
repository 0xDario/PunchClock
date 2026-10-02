using Microsoft.Data.Sqlite;

namespace PunchClock.Data.Sqlite;

/// <summary>Opens configured connections to one SQLite file.</summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    public SqliteDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // No pooling: the file handle is released as soon as a unit of work ends, so the
            // database can be backed up or copied whenever the kiosk is idle.
            Pooling = false,
            // Seconds Microsoft.Data.Sqlite keeps retrying while another writer holds the lock.
            DefaultTimeout = 30,
        }.ToString();
    }

    public string Path { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var pragma = connection.CreateCommand();
            // synchronous=FULL: a committed punch survives power loss, at a cost a kiosk never notices.
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;";
            await pragma.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
