using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PunchClock.Core.Audit;

namespace PunchClock.Data.Sqlite;

public sealed record Migration(int Version, string Name, string Sql)
{
    /// <summary>SHA-256 of the script with line endings normalised, so git autocrlf cannot change it.</summary>
    public string Checksum { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Sql.ReplaceLineEndings("\n"))));
}

public sealed class SchemaMismatchException(string message) : Exception(message);

/// <summary>
/// Applies the embedded <c>Migrations/NNNN_name.sql</c> scripts that the database has not seen,
/// in order, as the system account, and records each with its checksum in <c>schema_migrations</c>
/// (excluded from the schema fingerprint, not itself audited) plus a sealed <c>SCHEMA_MIGRATE</c>
/// audit event carrying that checksum and the resulting schema fingerprint.
/// Forward-only: an applied script that was later edited, a database migrated by a newer
/// build, or a live schema that no longer matches what the scripts create (a trigger dropped
/// with a generic SQLite tool, say) stops startup with <see cref="SchemaMismatchException"/>.
/// </summary>
public sealed partial class SchemaMigrator
{
    /// <summary>
    /// Oldest SQLite engine the schema supports: the audit-log design uses ORDER BY inside
    /// aggregates (3.44). The engine ships inside the SQLitePCLRaw bundle, so a package
    /// downgrade is the only way to fall below it; this turns that into a clear startup error.
    /// </summary>
    public static readonly Version MinimumSqliteVersion = new(3, 44, 0);

    // The schema's own schema_fingerprint_v, inlined: the view is part of the schema being
    // checked, so a tampered database could redefine it to return the expected value.
    // Excludes exactly SQLite's reserved sqlite_* objects (a prefix test: LIKE would treat
    // "_" as a wildcard and skip a trigger named sqlitex_...) and the runner's own table, by
    // type as well as name. Carriage returns are ignored so the line endings of the build
    // that created the database cannot change the result.
    private const string FingerprintQuery = """
        SELECT pc_sha256(json_group_array(json_array(type, name, tbl_name, replace(sql, char(13), '')) ORDER BY type, name))
          FROM sqlite_schema
         WHERE substr(name, 1, 7) <> 'sqlite_'
           AND NOT (type = 'table' AND name = 'schema_migrations');
        """;

    private static readonly Lazy<Task<string>> EmbeddedFingerprint = new(() => ComputeFingerprintAsync(LoadEmbedded()));

    private readonly SqliteDatabase _database;
    private readonly Lazy<Task<string>> _expectedFingerprint;

    public SchemaMigrator(SqliteDatabase database)
        : this(database, LoadEmbedded())
    {
        _expectedFingerprint = EmbeddedFingerprint;
    }

    internal SchemaMigrator(SqliteDatabase database, IEnumerable<Migration> migrations)
    {
        _database = database;
        Migrations = migrations.OrderBy(m => m.Version).ToList();
        if (Migrations.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new InvalidOperationException($"Duplicate migration version {duplicate.Key}.");
        }

        _expectedFingerprint = new(() => ComputeFingerprintAsync(Migrations));
    }

    public IReadOnlyList<Migration> Migrations { get; }

    /// <returns>The versions applied by this call; empty when already current.</returns>
    public async Task<IReadOnlyList<int>> MigrateAsync(CancellationToken ct = default)
    {
        var context = new AuditContext(_database.Client, AuditActor.System, "schema migration");
        await using var connection = await _database.OpenAsync(context, ct);
        EnsureSupportedEngine(connection.ServerVersion);

        // WAL lets readers proceed while a punch is being written. It is persistent, and
        // cannot be changed inside a transaction, so it is set here once.
        await ExecuteAsync(connection, null, "PRAGMA journal_mode = WAL;", ct);

        // One write transaction covers the version check and every pending script, so two
        // instances starting at once cannot both apply the same migration, and a failed
        // script leaves the database exactly as it was.
        await using var transaction = connection.BeginTransaction(deferred: false);

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version        INTEGER PRIMARY KEY,
                name           TEXT NOT NULL,
                checksum       TEXT NOT NULL,
                applied_at_utc TEXT NOT NULL
            ) STRICT;
            """, ct);

        var applied = await ReadAppliedAsync(connection, transaction, ct);
        foreach (var (version, checksum) in applied)
        {
            var known = Migrations.FirstOrDefault(m => m.Version == version)
                ?? throw new SchemaMismatchException(
                    $"The database has migration {version}, which this build does not know. It was created by a newer version of PunchClock.");
            if (!string.Equals(known.Checksum, checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new SchemaMismatchException(
                    $"Migration {version:D4}_{known.Name} differs from the script applied to this database. Applied migrations must never be edited.");
            }
        }

        var newlyApplied = new List<int>();
        foreach (var migration in Migrations.Where(m => !applied.ContainsKey(m.Version)))
        {
            await ExecuteAsync(connection, transaction, migration.Sql.ReplaceLineEndings("\n"), ct);

            await using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO schema_migrations (version, name, checksum, applied_at_utc)
                VALUES ($version, $name, $checksum, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            record.Parameters.AddWithValue("$version", migration.Version);
            record.Parameters.AddWithValue("$name", migration.Name);
            record.Parameters.AddWithValue("$checksum", migration.Checksum);
            await record.ExecuteNonQueryAsync(ct);

            await using var audit = connection.CreateCommand();
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO audit_log (actor_kind, actor_id, client, action, after_json, reason)
                SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'SCHEMA_MIGRATE',
                       json_object('version', $version, 'name', $name, 'checksum', $checksum,
                                   'schema_fingerprint', (SELECT fingerprint FROM schema_fingerprint_v)),
                       pc_ctx('reason');
                """;
            audit.Parameters.AddWithValue("$version", migration.Version);
            audit.Parameters.AddWithValue("$name", migration.Name);
            audit.Parameters.AddWithValue("$checksum", migration.Checksum);
            await audit.ExecuteNonQueryAsync(ct);
            newlyApplied.Add(migration.Version);
        }

        // Checked on every start, not only after a migration: the migration rows above prove
        // which scripts ran, not that their triggers are still there.
        EnsureFingerprint(await ReadFingerprintAsync(connection, transaction, ct), await _expectedFingerprint.Value);

        await transaction.CommitAsync(ct);
        return newlyApplied;
    }

    /// <summary>
    /// Throws <see cref="SchemaMismatchException"/> unless the live schema is exactly what this
    /// build's migrations create. Cheap (one hash over <c>sqlite_schema</c>); call it inside the
    /// write transaction so no DDL can land between the check and the writes it protects.
    /// </summary>
    public static async Task EnsureSchemaIntactAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct = default) =>
        EnsureFingerprint(await ReadFingerprintAsync(connection, transaction, ct), await EmbeddedFingerprint.Value);

    private static void EnsureFingerprint(string? actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new SchemaMismatchException(
                "The database schema differs from the one this version of PunchClock creates: a table, trigger, view or index was added, changed or removed outside the application. Nothing was written. Restore the database from a backup or have it examined.");
        }
    }

    /// <summary>The fingerprint a database has after exactly <paramref name="migrations"/>, built in memory.</summary>
    private static async Task<string> ComputeFingerprintAsync(IEnumerable<Migration> migrations)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await SqliteDatabase.ConfigureAsync(
            connection, new AuditContext(AuditContext.DefaultClient, AuditActor.System, "schema fingerprint"), default);
        await using var transaction = connection.BeginTransaction();
        foreach (var migration in migrations)
        {
            await ExecuteAsync(connection, transaction, migration.Sql.ReplaceLineEndings("\n"), default);
        }

        return await ReadFingerprintAsync(connection, transaction, default)
            ?? throw new InvalidOperationException("The schema fingerprint could not be computed.");
    }

    private static async Task<string?> ReadFingerprintAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = FingerprintQuery;
        return await command.ExecuteScalarAsync(ct) as string;
    }

    internal static void EnsureSupportedEngine(string sqliteVersion)
    {
        if (!Version.TryParse(sqliteVersion, out var version) || version < MinimumSqliteVersion)
        {
            throw new SchemaMismatchException(
                $"SQLite {sqliteVersion} is too old; PunchClock needs {MinimumSqliteVersion} or newer.");
        }
    }

    public static IReadOnlyList<Migration> LoadEmbedded()
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (ResourceName().Match(resource) is not { Success: true } match)
            {
                continue;
            }

            migrations.Add(new Migration(
                int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture),
                match.Groups["name"].Value,
                ReadResource(assembly, resource)));
        }

        return migrations;
    }

    private static async Task<Dictionary<int, string>> ReadAppliedAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version, checksum FROM schema_migrations;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var applied = new Dictionary<int, string>();
        while (await reader.ReadAsync(ct))
        {
            applied[reader.GetInt32(0)] = reader.GetString(1);
        }

        return applied;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded migration {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^PunchClock\.Migrations\.(?<version>\d{4})_(?<name>[A-Za-z0-9_]+)\.sql$")]
    private static partial Regex ResourceName();
}
