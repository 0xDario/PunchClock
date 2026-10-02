using Microsoft.Data.Sqlite;
using PunchClock.Data;

namespace PunchClock.Tests;

public class MigrationRunnerTests
{
    [Fact]
    public void Embedded_migrations_are_found_and_numbered()
    {
        var migrations = MigrationRunner.LoadEmbedded();

        Assert.Contains(migrations, m => m.Version == 1 && m.Name == "initial");
    }

    [Fact]
    public void Migrate_applies_all_then_is_idempotent()
    {
        using var db = new TestDatabase(migrate: false);
        var runner = new MigrationRunner(db.Database);

        var first = runner.Migrate();
        var second = runner.Migrate();

        Assert.Equal(runner.Migrations.Select(m => m.Version), first);
        Assert.Empty(second);
        Assert.Contains("employee", Tables(db));
        Assert.Contains("punch", Tables(db));
    }

    [Fact]
    public void Migrate_fails_when_an_applied_script_changed()
    {
        using var db = new TestDatabase(migrate: false);
        new MigrationRunner(db.Database, [new Migration(1, "one", "CREATE TABLE a (x INTEGER);")]).Migrate();

        var edited = new MigrationRunner(db.Database, [new Migration(1, "one", "CREATE TABLE a (y INTEGER);")]);

        var error = Assert.Throws<InvalidOperationException>(() => edited.Migrate());
        Assert.Contains("modified", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Migrate_fails_when_database_is_newer_than_build()
    {
        using var db = new TestDatabase(migrate: false);
        var one = new Migration(1, "one", "CREATE TABLE a (x INTEGER);");
        new MigrationRunner(db.Database, [one, new Migration(2, "two", "CREATE TABLE b (x INTEGER);")]).Migrate();

        Assert.Throws<InvalidOperationException>(() => new MigrationRunner(db.Database, [one]).Migrate());
    }

    [Fact]
    public void Failed_migration_rolls_back_and_is_not_recorded()
    {
        using var db = new TestDatabase(migrate: false);
        var runner = new MigrationRunner(db.Database,
        [
            new Migration(1, "ok", "CREATE TABLE a (x INTEGER);"),
            new Migration(2, "broken", "CREATE TABLE b (x INTEGER); SELECT * FROM missing_table;"),
        ]);

        Assert.Throws<SqliteException>(() => runner.Migrate());

        Assert.Contains("a", Tables(db));
        Assert.DoesNotContain("b", Tables(db));
        using var connection = db.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    private static List<string> Tables(TestDatabase db)
    {
        using var connection = db.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
