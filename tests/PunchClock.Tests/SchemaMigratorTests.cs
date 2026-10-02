using Microsoft.Data.Sqlite;
using PunchClock.Data.Sqlite;

namespace PunchClock.Tests;

public sealed class SchemaMigratorTests : DatabaseTest
{
    [Fact]
    public async Task Fresh_database_gets_every_embedded_migration()
    {
        var embedded = SchemaMigrator.LoadEmbedded();

        Assert.Contains(embedded, m => m.Version == 1 && m.Name == "initial");
        Assert.Equal(embedded.Count, await Db.ScalarAsync<long>("SELECT count(*) FROM schema_migration;"));
        Assert.Equal(2, await Db.ScalarAsync<long>("SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name IN ('employee', 'punch');"));
        Assert.Equal("wal", await Db.ScalarAsync<string>("PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task Rerunning_is_a_no_op()
    {
        Assert.Empty(await new SchemaMigrator(Db.Database).MigrateAsync());
    }

    [Fact]
    public async Task Edited_migration_stops_startup()
    {
        await Db.ExecuteAsync("UPDATE schema_migration SET checksum = 'deadbeef' WHERE version = 1;");

        var ex = await Assert.ThrowsAsync<SchemaMismatchException>(() => new SchemaMigrator(Db.Database).MigrateAsync());
        Assert.Contains("0001_initial", ex.Message);
    }

    [Fact]
    public async Task Database_from_a_newer_build_stops_startup()
    {
        await Db.ExecuteAsync("INSERT INTO schema_migration VALUES (9999, 'future', 'x', '2030-01-01T00:00:00.0000000Z');");

        await Assert.ThrowsAsync<SchemaMismatchException>(() => new SchemaMigrator(Db.Database).MigrateAsync());
    }

    [Fact]
    public async Task Failed_migration_rolls_back_every_pending_script()
    {
        var migrations = SchemaMigrator.LoadEmbedded().Concat(
        [
            new Migration(9001, "good", "CREATE TABLE extra (id INTEGER PRIMARY KEY);"),
            new Migration(9002, "bad", "CREATE TABLE broken (;"),
        ]);

        await Assert.ThrowsAsync<SqliteException>(() => new SchemaMigrator(Db.Database, migrations).MigrateAsync());

        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT count(*) FROM sqlite_schema WHERE name = 'extra';"));
        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT count(*) FROM schema_migration WHERE version > 9000;"));
    }

    [Fact]
    public async Task Bundled_sqlite_meets_the_schema_minimum()
    {
        var version = Version.Parse(await Db.ScalarAsync<string>("SELECT sqlite_version();"));

        Assert.True(version >= SchemaMigrator.MinimumSqliteVersion, $"Bundled SQLite is {version}.");
    }

    [Theory]
    [InlineData("3.41.2")]
    [InlineData("3.43.99")]
    [InlineData("garbage")]
    public void Older_sqlite_engines_stop_startup(string version)
    {
        Assert.Throws<SchemaMismatchException>(() => SchemaMigrator.EnsureSupportedEngine(version));
    }

    [Theory]
    [InlineData("3.44.0")]
    [InlineData("3.53.3")]
    public void Supported_sqlite_engines_pass(string version)
    {
        SchemaMigrator.EnsureSupportedEngine(version);
    }

    [Fact]
    public void Checksum_ignores_line_endings()
    {
        Assert.Equal(
            new Migration(1, "x", "SELECT 1;\r\nSELECT 2;\r\n").Checksum,
            new Migration(1, "x", "SELECT 1;\nSELECT 2;\n").Checksum);
    }

    [Fact]
    public async Task Foreign_keys_are_enforced()
    {
        await Assert.ThrowsAsync<SqliteException>(() => Db.ExecuteAsync("""
            INSERT INTO punch (employee_id, direction, occurred_at_utc, utc_offset_minutes, recorded_at_utc, source)
            VALUES (424242, 'IN', '2026-01-01T00:00:00.0000000Z', 0, '2026-01-01T00:00:00.0000000Z', 'KIOSK');
            """));
    }

    [Fact]
    public async Task Timestamps_must_be_fixed_width_utc()
    {
        var id = await Db.AddEmployeeAsync();

        await Assert.ThrowsAsync<SqliteException>(() => Db.ExecuteAsync("""
            INSERT INTO punch (employee_id, direction, occurred_at_utc, utc_offset_minutes, recorded_at_utc, source)
            VALUES ($id, 'IN', '2026-01-01 09:00', 0, '2026-01-01T00:00:00.0000000Z', 'KIOSK');
            """, ("$id", id)));
    }
}
