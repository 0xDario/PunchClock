using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PunchClock.Data.Sqlite;

/// <summary>
/// Opens connections to one SQLite file, configured the way the audit schema requires:
/// <c>pc_sha256</c>, <c>pc_utc_offset</c> and <c>pc_ctx</c> registered, foreign keys on, trusted schema on.
/// </summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    public SqliteDatabase(string path, string? client = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        Client = client ?? AuditContext.DefaultClient;
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

    /// <summary>Recorded as <c>client</c> in every audit row written through this database.</summary>
    public string Client { get; }

    /// <summary>A new audit context for this database's client.</summary>
    public AuditContext CreateContext() => new(Client);

    /// <summary>
    /// Opens a connection whose writes are attributed through <paramref name="context"/>. Without a
    /// context (or with no actor set on it) the connection can read everything and write nothing.
    /// </summary>
    public async Task<SqliteConnection> OpenAsync(AuditContext? context = null, CancellationToken ct = default)
    {
        context ??= CreateContext();
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await ConfigureAsync(connection, context, ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Registers the schema's functions and sets the pragmas it relies on.</summary>
    internal static async Task ConfigureAsync(SqliteConnection connection, AuditContext context, CancellationToken ct)
    {
        connection.CreateFunction("pc_sha256", (string? text) => Sha256Hex(text), isDeterministic: true);
        connection.CreateFunction("pc_utc_offset", (string? zoneId, string? utc) => UtcOffsetMinutes(zoneId, utc));
        connection.CreateFunction("pc_ctx", (string name) => context.Get(name));

        await using var pragma = connection.CreateCommand();
        // trusted_schema = ON: the schema's triggers and views call the two functions above;
        //   with it off SQLite refuses them ("unsafe use of pc_sha256()") and every write fails.
        // synchronous = FULL: a committed punch survives power loss.
        // recursive_triggers = ON: without it the REPLACE conflict resolution deletes rows without
        //   firing their BEFORE DELETE triggers, bypassing "never deleted" and the append-only log.
        pragma.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA recursive_triggers = ON;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 5000;
            PRAGMA trusted_schema = ON;
            """;
        await pragma.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Minutes east of UTC for a Windows time zone id at a UTC instant; null (never an exception,
    /// which would abort the statement with an unhelpful message) for an unknown zone or a null
    /// or unparseable argument. Windows ids also resolve on Linux and macOS through ICU.
    /// </summary>
    internal static long? UtcOffsetMinutes(string? zoneId, string? utc)
    {
        if (zoneId is null || utc is null
            || !TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone)
            || !DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
        {
            return null;
        }

        return (long)zone.GetUtcOffset(instant).TotalMinutes;
    }

    internal static string? Sha256Hex(string? text) =>
        text is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
