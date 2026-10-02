using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

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
/// in order, and records each with its checksum in <c>schema_migration</c>.
/// Forward-only: an applied script that was later edited, or a database migrated by a newer
/// build, stops startup with <see cref="SchemaMismatchException"/>.
/// </summary>
public sealed partial class SchemaMigrator
{
    private readonly SqliteDatabase _database;

    public SchemaMigrator(SqliteDatabase database)
        : this(database, LoadEmbedded())
    {
    }

    internal SchemaMigrator(SqliteDatabase database, IEnumerable<Migration> migrations)
    {
        _database = database;
        Migrations = migrations.OrderBy(m => m.Version).ToList();
        if (Migrations.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new InvalidOperationException($"Duplicate migration version {duplicate.Key}.");
        }
    }

    public IReadOnlyList<Migration> Migrations { get; }

    /// <returns>The versions applied by this call; empty when already current.</returns>
    public async Task<IReadOnlyList<int>> MigrateAsync(CancellationToken ct = default)
    {
        await using var connection = await _database.OpenAsync(ct);

        // WAL lets readers proceed while a punch is being written. It is persistent, and
        // cannot be changed inside a transaction, so it is set here once.
        await ExecuteAsync(connection, null, "PRAGMA journal_mode = WAL;", ct);

        // One write transaction covers the version check and every pending script, so two
        // instances starting at once cannot both apply the same migration, and a failed
        // script leaves the database exactly as it was.
        await using var transaction = connection.BeginTransaction(deferred: false);

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS schema_migration (
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
            await ExecuteAsync(connection, transaction, migration.Sql, ct);

            await using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO schema_migration (version, name, checksum, applied_at_utc)
                VALUES ($version, $name, $checksum, $at);
                """;
            record.Parameters.AddWithValue("$version", migration.Version);
            record.Parameters.AddWithValue("$name", migration.Name);
            record.Parameters.AddWithValue("$checksum", migration.Checksum);
            record.Parameters.AddWithValue("$at", SqliteTime.ToText(DateTimeOffset.UtcNow));
            await record.ExecuteNonQueryAsync(ct);
            newlyApplied.Add(migration.Version);
        }

        await transaction.CommitAsync(ct);
        return newlyApplied;
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
        command.CommandText = "SELECT version, checksum FROM schema_migration;";
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
