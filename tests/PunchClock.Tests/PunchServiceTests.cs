using Microsoft.Data.Sqlite;
using PunchClock.Core.Audit;
using PunchClock.Core.Punches;
using PunchClock.Core.Security;
using PunchClock.Data.Sqlite;

namespace PunchClock.Tests;

public sealed class PunchServiceTests : DatabaseTest
{
    [Fact]
    public async Task Punch_in_then_out_records_two_events()
    {
        var id = await Db.AddEmployeeAsync();

        var punchIn = await Db.Punches.PunchAsync(id, "1234", PunchDirection.In);
        Db.Clock.Advance(TimeSpan.FromSeconds(30));
        var punchOut = await Db.Punches.PunchAsync(id, "1234", PunchDirection.Out);

        Assert.True(punchIn.Accepted);
        Assert.True(punchOut.Accepted);
        Assert.Equal(PunchDirection.In, punchIn.Punch!.Direction);
        Assert.Equal(PunchDirection.Out, punchOut.Punch!.Direction);
        Assert.InRange(punchOut.Punch.OccurredAtUtc - punchIn.Punch.OccurredAtUtc, TimeSpan.FromMilliseconds(29_999), TimeSpan.FromMilliseconds(30_001));
        Assert.Equal(PunchSource.Kiosk, punchOut.Punch.Source);
        Assert.Equal(2, await CountPunchesAsync(id));
        Assert.Equal(ClockStatus.Out, await Db.Employees.GetStatusAsync(id));
    }

    [Fact]
    public async Task Punching_in_twice_is_rejected_with_the_open_punch()
    {
        var id = await Db.AddEmployeeAsync();
        var first = await Db.Punches.PunchAsync(id, "1234", PunchDirection.In);

        var second = await Db.Punches.PunchAsync(id, "1234", PunchDirection.In);

        Assert.False(second.Accepted);
        Assert.Equal(PunchRejection.AlreadyPunchedIn, second.Rejection);
        Assert.Equal(first.Punch!.Id, second.LastPunch!.Id);
        Assert.Equal(1, await CountPunchesAsync(id));
    }

    [Fact]
    public async Task Punching_out_while_out_is_rejected()
    {
        var id = await Db.AddEmployeeAsync();

        var result = await Db.Punches.PunchAsync(id, "1234", PunchDirection.Out);

        Assert.Equal(PunchRejection.NotPunchedIn, result.Rejection);
        Assert.Equal(0, await CountPunchesAsync(id));
    }

    [Fact]
    public async Task Wrong_pin_records_nothing_but_the_failed_attempt()
    {
        var id = await Db.AddEmployeeAsync("0123");

        var result = await Db.Punches.PunchAsync(id, "123", PunchDirection.In);

        Assert.Equal(PunchRejection.InvalidPin, result.Rejection);
        Assert.Equal(0, await CountPunchesAsync(id));
        Assert.Equal(1, await Db.ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE action = 'AUTH_PIN_FAILED' AND actor_kind = 'employee' AND actor_id = $id;",
            ("$id", id)));
    }

    [Fact]
    public async Task Inactive_and_unknown_employees_cannot_punch()
    {
        var id = await Db.AddEmployeeAsync();
        await Db.Employees.SetActiveAsync(AuditActor.System, id, isActive: false);

        Assert.Equal(PunchRejection.EmployeeInactive, (await Db.Punches.PunchAsync(id, "1234", PunchDirection.In)).Rejection);
        Assert.Equal(PunchRejection.EmployeeNotFound, (await Db.Punches.PunchAsync(999, "1234", PunchDirection.In)).Rejection);
    }

    [Fact]
    public async Task Clock_running_behind_the_last_punch_is_rejected()
    {
        var id = await Db.AddEmployeeAsync();
        await Db.Punches.PunchAsync(id, "1234", PunchDirection.In);
        Db.Clock.Advance(TimeSpan.FromSeconds(-30));

        var result = await Db.Punches.PunchAsync(id, "1234", PunchDirection.Out);

        Assert.Equal(PunchRejection.ClockBehindLastPunch, result.Rejection);
        Assert.Equal(1, await CountPunchesAsync(id));
    }

    [Fact]
    public async Task Database_rejects_a_kiosk_punch_far_from_its_own_clock()
    {
        // The service trusts the app clock; the schema re-checks it against the database clock.
        var id = await Db.AddEmployeeAsync();
        Db.Clock.Advance(TimeSpan.FromHours(3));

        var ex = await Assert.ThrowsAsync<SqliteException>(() => Db.Punches.PunchAsync(id, "1234", PunchDirection.In));

        Assert.Contains("current time", ex.Message);
        Assert.Equal(0, await CountPunchesAsync(id));
    }

    [Fact]
    public async Task Database_rejects_a_kiosk_punch_for_someone_else()
    {
        var ana = await Db.AddEmployeeAsync(first: "Ana");
        var ben = await Db.AddEmployeeAsync(first: "Ben");

        var ex = await Assert.ThrowsAsync<SqliteException>(() => Db.ExecuteAsync(AuditActor.ForEmployee(ana), """
            INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source)
            VALUES ($ben, 'IN', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 0, 'kiosk');
            """, ("$ben", ben)));

        Assert.Contains("punching employee", ex.Message);
    }

    [Fact]
    public async Task State_follows_effective_punch_time_not_row_order()
    {
        var manager = await Db.AddManagerAsync();
        var id = await Db.AddEmployeeAsync();
        var punchIn = (await Db.Punches.PunchAsync(id, "1234", PunchDirection.In)).Punch!;
        Assert.Equal(ClockStatus.In, await Db.Employees.GetStatusAsync(id));

        // A backdated OUT inserted after it: higher id, earlier time. The kiosk IN stays the latest.
        await Db.CorrectAsync(new NewCorrection(CorrectionAction.Add, id, null, PunchDirection.Out,
            SqliteTime.Truncate(punchIn.OccurredAtUtc.AddHours(-1)), 0, "Missing punch-out from yesterday", manager.Id));
        Assert.Equal(ClockStatus.In, await Db.Employees.GetStatusAsync(id));

        // Voiding the kiosk IN leaves the earlier OUT as the latest effective punch.
        await Db.CorrectAsync(new NewCorrection(CorrectionAction.Void, id, punchIn.Id, null, null, null,
            "Punched in by mistake, was off shift", manager.Id));
        Assert.Equal(ClockStatus.Out, await Db.Employees.GetStatusAsync(id));
        Assert.True((await Db.Punches.PunchAsync(id, "1234", PunchDirection.In)).Accepted);
    }

    [Fact]
    public async Task Offset_comes_from_the_site_time_zone()
    {
        var admin = await Db.AddAdminAsync();
        var toronto = OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/Toronto";
        await Db.Site.SetTimeZoneAsync(admin, toronto);
        var id = await Db.AddEmployeeAsync();

        var punch = (await Db.Punches.PunchAsync(id, "1234", PunchDirection.In)).Punch!;

        var expected = (int)TimeZoneInfo.FindSystemTimeZoneById(toronto).GetUtcOffset(punch.OccurredAtUtc).TotalMinutes;
        Assert.Equal(expected, punch.UtcOffsetMinutes);
        Assert.Equal(SqliteTime.ToText(punch.OccurredAtUtc),
            await Db.ScalarAsync<string>("SELECT occurred_utc FROM punch WHERE id = $id;", ("$id", punch.Id)));
    }

    [Fact]
    public async Task Unset_site_zone_falls_back_to_the_machine_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+0530", TimeSpan.FromMinutes(330), "Test", "Test");
        var punches = new PunchService(Db.Store, TestDatabase.FastHasher, new ManualTimeProvider(DateTimeOffset.UtcNow, zone));
        var id = await Db.AddEmployeeAsync();

        var punch = (await punches.PunchAsync(id, "1234", PunchDirection.In)).Punch!;

        Assert.Equal(330, punch.UtcOffsetMinutes);
    }

    [Fact]
    public async Task Punches_are_immutable()
    {
        var id = await Db.AddEmployeeAsync();
        var punch = (await Db.Punches.PunchAsync(id, "1234", PunchDirection.In)).Punch!;

        var update = await Assert.ThrowsAsync<SqliteException>(() => Db.ExecuteAsync(AuditActor.System,
            "UPDATE punch SET occurred_utc = '2020-01-01T00:00:00.000Z' WHERE id = $id;", ("$id", punch.Id)));
        var delete = await Assert.ThrowsAsync<SqliteException>(() => Db.ExecuteAsync(AuditActor.System,
            "DELETE FROM punch WHERE id = $id;", ("$id", punch.Id)));

        Assert.Contains("immutable", update.Message);
        Assert.Contains("immutable", delete.Message);
        Assert.Equal(1, await CountPunchesAsync(id));
    }

    [Fact]
    public async Task Concurrent_punch_ins_record_exactly_one()
    {
        var id = await Db.AddEmployeeAsync();
        // A slow PIN check widens the window between "read state" and "insert" so that,
        // without the up-front write lock, every attempt would see "out" before any insert.
        var punches = new PunchService(Db.Store, new SlowPinHasher(TestDatabase.FastHasher), Db.Clock);
        using var start = new ManualResetEventSlim();

        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            return await punches.PunchAsync(id, "1234", PunchDirection.In);
        })).ToList();
        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, r => r.Accepted);
        Assert.All(results.Where(r => !r.Accepted), r => Assert.Equal(PunchRejection.AlreadyPunchedIn, r.Rejection));
        Assert.Equal(1, await CountPunchesAsync(id));
    }

    [Fact]
    public async Task Uncommitted_unit_of_work_writes_nothing()
    {
        var id = await Db.AddEmployeeAsync();

        await using (var uow = await Db.Store.BeginAsync())
        {
            uow.ActAs(AuditActor.ForEmployee(id));
            await uow.AppendKioskPunchAsync(id, PunchDirection.In, DateTimeOffset.UtcNow, 0);
        }

        Assert.Equal(0, await CountPunchesAsync(id));
        Assert.Empty(await Db.VerifyAsync());
    }

    private Task<long> CountPunchesAsync(long employeeId) =>
        Db.ScalarAsync<long>("SELECT count(*) FROM punch WHERE employee_id = $id;", ("$id", employeeId));

    private sealed class SlowPinHasher(IPinHasher inner) : IPinHasher
    {
        public string Hash(string pin) => inner.Hash(pin);

        public bool Verify(string pin, string encodedHash)
        {
            Thread.Sleep(50);
            return inner.Verify(pin, encodedHash);
        }
    }
}
