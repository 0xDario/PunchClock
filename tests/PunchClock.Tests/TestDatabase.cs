using Microsoft.Data.Sqlite;
using PunchClock.Core.Employees;
using PunchClock.Core.Punches;
using PunchClock.Core.Security;
using PunchClock.Data.Sqlite;

namespace PunchClock.Tests;

/// <summary>A migrated SQLite file in the temp folder, deleted on dispose.</summary>
public sealed class TestDatabase : IAsyncLifetime
{
    // Low cost keeps the suite fast; the encoded hash carries its own iteration count.
    public static readonly Pbkdf2PinHasher FastHasher = new(iterations: 1_000);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "punchclock-tests", Guid.NewGuid().ToString("N"));

    public TestDatabase()
    {
        Directory.CreateDirectory(_directory);
        Database = new SqliteDatabase(Path.Combine(_directory, "punchclock.db"));
        Store = new SqlitePunchClockStore(Database);
        Employees = new EmployeeService(Store, FastHasher, Clock);
        Punches = new PunchService(Store, FastHasher, Clock);
    }

    public SqliteDatabase Database { get; }

    public SqlitePunchClockStore Store { get; }

    public ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 3, 9, 13, 0, 0, TimeSpan.Zero));

    public EmployeeService Employees { get; }

    public PunchService Punches { get; }

    public async ValueTask InitializeAsync() => await new SchemaMigrator(Database).MigrateAsync();

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleaner gets the rest.
        }

        return ValueTask.CompletedTask;
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await Database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await Database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    public Task<long> AddEmployeeAsync(string pin = "1234", string first = "Ada", string last = "Lovelace") =>
        Employees.CreateAsync(first, last, pin);

    public async Task AppendRawAsync(long employeeId, PunchDirection direction, DateTimeOffset at)
    {
        await using var uow = await Store.BeginAsync();
        await uow.AppendPunchAsync(new NewPunch(employeeId, direction, at, 0, at, PunchSource.Import));
        await uow.CommitAsync();
    }
}

public sealed class ManualTimeProvider(DateTimeOffset utcNow, TimeZoneInfo? zone = null) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override TimeZoneInfo LocalTimeZone { get; } = zone ?? TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Gives each test its own freshly migrated database.</summary>
public abstract class DatabaseTest : IAsyncLifetime
{
    protected TestDatabase Db { get; } = new();

    public ValueTask InitializeAsync() => Db.InitializeAsync();

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
