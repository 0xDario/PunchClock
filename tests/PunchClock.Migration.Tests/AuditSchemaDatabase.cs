using Microsoft.Data.Sqlite;
using PunchClock.Migration.Database;

namespace PunchClock.Migration.Tests;

/// <summary>A temp SQLite file with docs/database/schema.sql applied, as the app will create it.</summary>
public sealed class AuditSchemaDatabase : IAsyncLifetime
{
    readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "punchclock-migration-tests", Guid.NewGuid().ToString("N"));

    public string Path => System.IO.Path.Combine(_directory, "punchclock.db");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var schema = await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "schema.sql"));

        await using (var create = new SqliteConnection($"Data Source={Path};Pooling=False"))
        {
            await create.OpenAsync();
            await using var wal = create.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            await wal.ExecuteNonQueryAsync();
        }

        await using var c = await AuditedConnection.OpenAsync(Path, "user", 1, "tests", "schema", default);
        await using var tx = c.BeginTransaction();
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = schema;
        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var c = new SqliteConnection($"Data Source={Path};Mode=ReadOnly;Pooling=False");
        await c.OpenAsync();
        c.CreateFunction("pc_sha256", (string? s) => s is null ? null : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s))), isDeterministic: true);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    public async Task<List<object?[]>> RowsAsync(string sql)
    {
        await using var c = new SqliteConnection($"Data Source={Path};Mode=ReadOnly;Pooling=False");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await r.ReadAsync())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++)
                row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }
}
