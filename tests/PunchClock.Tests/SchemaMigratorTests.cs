using Microsoft.Data.Sqlite;
using PunchClock.Core.Audit;
using PunchClock.Data.Sqlite;

namespace PunchClock.Tests;

public sealed class SchemaMigratorTests : DatabaseTest
{
    [Fact]
    public async Task Fresh_database_gets_the_audit_schema()
    {
        var embedded = SchemaMigrator.LoadEmbedded();

        Assert.Contains(embedded, m => m.Version == 1 && m.Name == "audit_schema");
        Assert.Equal(embedded.Count, await Db.ScalarAsync<long>("SELECT count(*) FROM schema_migrations;"));
        Assert.Equal(11, await Db.ScalarAsync<long>("""
            SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name IN
              ('site_setting', 'employee', 'app_user', 'import_batch', 'legacy_employee_raw', 'legacy_shift_raw',
               'migration_issue', 'punch', 'punch_correction', 'audit_checkpoint', 'audit_log');
            """));
        Assert.Equal("wal", await Db.ScalarAsync<string>("PRAGMA journal_mode;"));
        Assert.Equal(["system", "migration"], await Db.ColumnAsync("SELECT username FROM app_user ORDER BY id;"));
    }

    [Fact]
    public async Task Migration_is_audited_with_checksum_and_fingerprint()
    {
        var migration = SchemaMigrator.LoadEmbedded().Single(m => m.Version == 1);

        var detail = await Db.ScalarAsync<string>("""
            SELECT after_json FROM audit_log WHERE action = 'SCHEMA_MIGRATE' AND actor_kind = 'user' AND actor_id = 1;
            """);

        Assert.Contains(migration.Checksum, detail);
        Assert.Contains(await Db.ScalarAsync<string>("SELECT fingerprint FROM schema_fingerprint_v;"), detail);
        Assert.Empty(await Db.VerifyAsync());
    }

    [Fact]
    public async Task Rerunning_is_a_no_op()
    {
        Assert.Empty(await new SchemaMigrator(Db.Database).MigrateAsync());
    }

    [Fact]
    public async Task Edited_migration_stops_startup()
    {
        // schema_migrations is the runner's own table, outside the audited schema.
        await Db.ExecuteAsync(null, "UPDATE schema_migrations SET checksum = 'deadbeef' WHERE version = 1;");

        var ex = await Assert.ThrowsAsync<SchemaMismatchException>(() => new SchemaMigrator(Db.Database).MigrateAsync());
        Assert.Contains("0001_audit_schema", ex.Message);
    }

    [Fact]
    public async Task Database_from_a_newer_build_stops_startup()
    {
        await Db.ExecuteAsync(null, "INSERT INTO schema_migrations VALUES (9999, 'future', 'x', '2030-01-01T00:00:00.000Z');");

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
        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT count(*) FROM schema_migrations WHERE version > 9000;"));
    }

    [Fact]
    public void Embedded_schema_is_the_reviewed_design_file()
    {
        // Applied migrations are immutable, so a change to docs/database/schema.sql after the
        // first install has to ship as a new migration, not an edit to 0001.
        var migration = SchemaMigrator.LoadEmbedded().Single(m => m.Version == 1);

        Assert.StartsWith("-- PunchClock schema v1", migration.Sql);
        Assert.Contains("'OUT_OF_ORDER_ID', 'CROSSES_DST'", migration.Sql);
    }

    [Fact]
    public void Checksum_ignores_line_endings()
    {
        Assert.Equal(
            new Migration(1, "x", "SELECT 1;\r\nSELECT 2;\r\n").Checksum,
            new Migration(1, "x", "SELECT 1;\nSELECT 2;\n").Checksum);
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
    public async Task Connection_settings_match_the_schema_requirements()
    {
        await using var connection = await Db.Database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT foreign_keys FROM pragma_foreign_keys), (SELECT synchronous FROM pragma_synchronous), (SELECT trusted_schema FROM pragma_trusted_schema);";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(2, reader.GetInt64(1)); // FULL
        Assert.Equal(1, reader.GetInt64(2));
    }

    [Fact]
    public async Task Timestamps_must_be_utc_with_milliseconds()
    {
        var id = await Db.AddEmployeeAsync();

        var ex = await Assert.ThrowsAsync<SqliteException>(() => Db.ExecuteAsync(AuditActor.ForEmployee(id), """
            INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source)
            VALUES ($id, 'IN', strftime('%Y-%m-%dT%H:%M:%S', 'now'), 0, 'kiosk');
            """, ("$id", id)));
        Assert.Contains("CHECK", ex.Message);
    }

    [Fact]
    public void Sqlite_time_round_trips_at_millisecond_precision()
    {
        var value = new DateTimeOffset(2026, 10, 2, 19, 30, 15, 123, TimeSpan.FromHours(-4)).AddTicks(4567);

        var text = SqliteTime.ToText(value);

        Assert.Equal("2026-10-02T23:30:15.123Z", text);
        Assert.Equal(SqliteTime.Truncate(value), SqliteTime.Parse(text));
    }
}
