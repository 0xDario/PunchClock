using Microsoft.Data.Sqlite;
using PunchClock.Core.Audit;
using PunchClock.Data.Sqlite;

namespace PunchClock.Migration.Tests;

/// <summary>A temp SQLite file with the app's migrations (the audit schema) applied.</summary>
/// <param name="siteZone">Site time zone to set, as an admin would before importing; null leaves it unset.</param>
public sealed class AuditSchemaDatabase(string? siteZone = "America/Toronto") : IAsyncLifetime
{
    readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "punchclock-migration-tests", Guid.NewGuid().ToString("N"));

    public string Path => System.IO.Path.Combine(_directory, "punchclock.db");

    /// <summary>Created and migrated exactly as the app and the importer do it.</summary>
    public SqliteDatabase Database { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Database = await PunchClockDatabase.OpenAndMigrateAsync(Path);
        if (siteZone is not null)
            await SetSiteZoneAsync(siteZone);
    }

    public async Task SetSiteZoneAsync(string zone)
    {
        await using var c = await Database.OpenAsync(new AuditContext(Database.Client, AuditActor.System, "test setup"));
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE site_setting SET value = $zone WHERE key = 'time_zone_id';";
        cmd.Parameters.AddWithValue("$zone", zone);
        await cmd.ExecuteNonQueryAsync();
    }

    // Through the app's own connection setup, so every function the schema's views call is registered.
    Task<SqliteConnection> OpenAsync() =>
        Database.OpenAsync(new AuditContext(Database.Client, AuditActor.Migration, "test read"));

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var c = await OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    public async Task<List<object?[]>> RowsAsync(string sql)
    {
        await using var c = await OpenAsync();
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
