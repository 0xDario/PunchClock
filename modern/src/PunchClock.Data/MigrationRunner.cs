using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PunchClock.Data;

public sealed record Migration(int Version, string Name, string Sql)
{
    public string Checksum { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sql)));
}

/// <summary>
/// Applies embedded <c>Migrations/NNNN_name.sql</c> scripts in order, each in its own
/// transaction, and records them in <c>schema_migrations</c>. Fails if an applied
/// script was edited afterwards: migrations are forward-only.
/// </summary>
public sealed partial class MigrationRunner
{
    private readonly SqliteDatabase _database;
    private readonly IReadOnlyList<Migration> _migrations;

    public MigrationRunner(SqliteDatabase database)
        : this(database, LoadEmbedded())
    {
    }

    public MigrationRunner(SqliteDatabase database, IReadOnlyList<Migration> migrations)
    {
        _database = database;
        _migrations = migrations.OrderBy(m => m.Version).ToList();
        var duplicate = _migrations.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Duplicate migration version {duplicate.Key}.");
        }
    }

    public IReadOnlyList<Migration> Migrations => _migrations;

    /// <summary>Returns the versions applied by this call.</summary>
    public IReadOnlyList<int> Migrate()
    {
        using var connection = _database.Open();
        Execute(connection, null, "PRAGMA journal_mode = WAL;");
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version     INTEGER PRIMARY KEY,
                name        TEXT NOT NULL,
                checksum    TEXT NOT NULL,
                applied_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            ) STRICT;
            """);

        var applied = ReadApplied(connection);
        foreach (var (version, checksum) in applied)
        {
            var known = _migrations.FirstOrDefault(m => m.Version == version)
                ?? throw new InvalidOperationException(
                    $"Database has migration {version}, which this build does not know. Is the app older than the database?");
            if (!string.Equals(known.Checksum, checksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Migration {version} ({known.Name}) was modified after it was applied.");
            }
        }

        var newlyApplied = new List<int>();
        foreach (var migration in _migrations.Where(m => !applied.ContainsKey(m.Version)))
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, migration.Sql);
            using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_migrations (version, name, checksum) VALUES ($v, $n, $c);";
            record.Parameters.AddWithValue("$v", migration.Version);
            record.Parameters.AddWithValue("$n", migration.Name);
            record.Parameters.AddWithValue("$c", migration.Checksum);
            record.ExecuteNonQuery();
            transaction.Commit();
            newlyApplied.Add(migration.Version);
        }

        return newlyApplied;
    }

    public static IReadOnlyList<Migration> LoadEmbedded()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = ResourceName().Match(resource);
            if (!match.Success)
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

    private static Dictionary<int, string> ReadApplied(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, checksum FROM schema_migrations;";
        using var reader = command.ExecuteReader();
        var applied = new Dictionary<int, string>();
        while (reader.Read())
        {
            applied[reader.GetInt32(0)] = reader.GetString(1);
        }

        return applied;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing resource {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // Normalise line endings so checksums match across git autocrlf settings.
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\.Migrations\.(?<version>\d{4})_(?<name>[A-Za-z0-9_]+)\.sql$")]
    private static partial Regex ResourceName();
}
