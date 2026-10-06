using Microsoft.Data.Sqlite;
using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;
using PunchClock.Core.Employees;
using PunchClock.Core.Punches;
using PunchClock.Core.Security;
using PunchClock.Core.Site;
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
        Database = new SqliteDatabase(Path.Combine(_directory, "punchclock.db"), "TEST-PC/0.0.0");
        Store = new SqlitePunchClockStore(Database);
        Employees = new EmployeeService(Store, FastHasher);
        Punches = new PunchService(Store, FastHasher, Clock);
        Accounts = new AccountService(Store, FastHasher);
        Site = new SiteSettingsService(Store);
        Corrections = new PunchCorrectionService(Store, Clock);
    }

    public SqliteDatabase Database { get; }

    public SqlitePunchClockStore Store { get; }

    /// <summary>
    /// Starts just behind the real time: the schema rejects kiosk punches later than the database
    /// clock or more than 120 s behind it, so tests may advance a few seconds but not minutes.
    /// </summary>
    public ManualTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow.AddSeconds(-30));

    public EmployeeService Employees { get; }

    public PunchService Punches { get; }

    public AccountService Accounts { get; }

    public SiteSettingsService Site { get; }

    public PunchCorrectionService Corrections { get; }

    public ValueTask InitializeAsync() => InitializeAsync(TimeZoneInfo.Utc.Id);

    /// <param name="siteZone">Site time zone to configure, as an install would; null leaves it unset.</param>
    public async ValueTask InitializeAsync(string? siteZone)
    {
        await new SchemaMigrator(Database).MigrateAsync();
        if (siteZone is not null)
        {
            await ExecuteAsync(AuditActor.System, "UPDATE site_setting SET value = $zone WHERE key = 'time_zone_id';", ("$zone", siteZone));
        }
    }

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

    /// <summary>Reads through a connection with no actor set (reads need none).</summary>
    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await Database.OpenAsync();
        await using var command = Command(connection, sql, parameters);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    public async Task<IReadOnlyList<string>> ColumnAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await Database.OpenAsync();
        await using var command = Command(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.IsDBNull(0) ? "NULL" : reader.GetValue(0).ToString()!);
        }

        return values;
    }

    /// <summary>Writes as <paramref name="actor"/>; null means a connection with no actor at all.</summary>
    public async Task ExecuteAsync(AuditActor? actor, string sql, params (string Name, object Value)[] parameters)
    {
        var context = Database.CreateContext();
        context.Actor = actor;
        await using var connection = await Database.OpenAsync(context);
        await using var command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    public Task<long> AddEmployeeAsync(string pin = "1234", string first = "Ada", string last = "Lovelace") =>
        Employees.CreateAsync(AuditActor.System, first, last, pin, pinMustChange: false);

    public async Task<AppUser> AddAdminAsync(string username = "owner", string password = "correct horse battery")
    {
        if (await Accounts.NeedsFirstAdminAsync())
        {
            return await Accounts.CreateFirstAdminAsync(username, "Owner", password);
        }

        await using var uow = await Store.BeginAsync();
        uow.ActAs(AuditActor.System);
        var id = await uow.AddUserAsync(new NewAppUser(username, username, UserRole.Admin, FastHasher.Hash(password)));
        var user = (await uow.FindUserAsync(id))!;
        await uow.CommitAsync();
        return user;
    }

    public async Task<AppUser> AddManagerAsync(string username = "manager")
    {
        var admin = await AddAdminAsync("admin-for-" + username);
        await using var uow = await Store.BeginAsync();
        uow.ActAs(AuditActor.ForUser(admin.Id));
        var id = await uow.AddUserAsync(new NewAppUser(username, "Manager", UserRole.Manager, FastHasher.Hash("manager password")));
        var user = (await uow.FindUserAsync(id))!;
        await uow.CommitAsync();
        return user;
    }

    public async Task<long> CorrectAsync(NewCorrection correction)
    {
        await using var uow = await Store.BeginAsync();
        uow.ActAs(AuditActor.ForUser(correction.ActorUserId));
        var id = await uow.AddCorrectionAsync(correction);
        await uow.CommitAsync();
        return id;
    }

    /// <summary>Rows returned by every verifier view; empty on an intact database.</summary>
    public Task<IReadOnlyList<string>> VerifyAsync() => AuditVerifier.FindProblemsAsync(Database);

    private static SqliteCommand Command(SqliteConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
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

    /// <summary>The site zone the database starts with; null to start unset like a fresh install.</summary>
    protected virtual string? SiteZone => TimeZoneInfo.Utc.Id;

    public ValueTask InitializeAsync() => Db.InitializeAsync(SiteZone);

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
