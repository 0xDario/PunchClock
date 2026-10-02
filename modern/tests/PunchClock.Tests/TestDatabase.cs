using PunchClock.Core.Abstractions;
using Microsoft.Data.Sqlite;
using PunchClock.Data;

namespace PunchClock.Tests;

/// <summary>A migrated SQLite file in the temp folder, deleted on dispose.</summary>
public sealed class TestDatabase : IDisposable
{
    public TestDatabase(bool migrate = true)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"punchclock-test-{Guid.NewGuid():N}.db");
        Database = SqliteDatabase.ForFile(Path);
        if (migrate)
        {
            new MigrationRunner(Database).Migrate();
        }
    }

    public string Path { get; }

    public SqliteDatabase Database { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(Path + suffix);
        }
    }
}

public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public void Advance(TimeSpan by) => Now += by;
}
