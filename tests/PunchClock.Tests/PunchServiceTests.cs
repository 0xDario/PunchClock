using Microsoft.Data.Sqlite;
using PunchClock.Core.Punches;
using PunchClock.Core.Security;

namespace PunchClock.Tests;

public sealed class PunchServiceTests : DatabaseTest
{
    [Fact]
    public async Task Punch_in_then_out_records_two_events()
    {
        var id = await Db.AddEmployeeAsync();

        var punchIn = await Db.Punches.PunchAsync(id, "1234", PunchDirection.In);
        Db.Clock.Advance(TimeSpan.FromHours(8));
        var punchOut = await Db.Punches.PunchAsync(id, "1234", PunchDirection.Out);

        Assert.True(punchIn.Accepted);
        Assert.True(punchOut.Accepted);
        Assert.Equal(PunchDirection.In, punchIn.Punch!.Direction);
        Assert.Equal(PunchDirection.Out, punchOut.Punch!.Direction);
        Assert.Equal(TimeSpan.FromHours(8), punchOut.Punch.OccurredAtUtc - punchIn.Punch.OccurredAtUtc);
        Assert.Equal(PunchSource.Kiosk, punchOut.Punch.Source);
        Assert.Equal(2, await CountPunchesAsync(id));
        Assert.Equal(ClockStatus.Out, await Db.Employees.GetStatusAsync(id));
    }

    [Fact]
    public async Task Punching_in_twice_is_rejected_with_the_open_punch()
    {
        var id = await Db.AddEmployeeAsync();
        var first = await Db.Punches.PunchAsync(id, "1234", PunchDirection.In);
        Db.Clock.Advance(TimeSpan.FromHours(20));

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
    public async Task Wrong_pin_records_nothing()
    {
        var id = await Db.AddEmployeeAsync("0123");

        var result = await Db.Punches.PunchAsync(id, "123", PunchDirection.In);

        Assert.Equal(PunchRejection.InvalidPin, result.Rejection);
        Assert.Equal(0, await CountPunchesAsync(id));
    }

    [Fact]
    public async Task Inactive_and_unknown_employees_cannot_punch()
    {
        var id = await Db.AddEmployeeAsync();
        await Db.ExecuteAsync("UPDATE employee SET is_active = 0 WHERE id = $id;", ("$id", id));

        Assert.Equal(PunchRejection.EmployeeInactive, (await Db.Punches.PunchAsync(id, "1234", PunchDirection.In)).Rejection);
        Assert.Equal(PunchRejection.EmployeeNotFound, (await Db.Punches.PunchAsync(999, "1234", PunchDirection.In)).Rejection);
    }

    [Fact]
    public async Task Clock_running_behind_the_last_punch_is_rejected()
    {
        var id = await Db.AddEmployeeAsync();
        await Db.Punches.PunchAsync(id, "1234", PunchDirection.In);
        Db.Clock.Advance(TimeSpan.FromHours(-1));

        var result = await Db.Punches.PunchAsync(id, "1234", PunchDirection.Out);

        Assert.Equal(PunchRejection.ClockBehindLastPunch, result.Rejection);
        Assert.Equal(1, await CountPunchesAsync(id));
    }

    [Fact]
    public async Task State_follows_punch_time_not_row_order()
    {
        // Rows written out of time order, as an import or a later correction would:
        // OUT 17:00 gets the lower id, IN 09:00 the higher one.
        var id = await Db.AddEmployeeAsync();
        var day = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        await Db.AppendRawAsync(id, PunchDirection.Out, day.AddHours(17));
        await Db.AppendRawAsync(id, PunchDirection.In, day.AddHours(9));

        Assert.Equal(ClockStatus.Out, await Db.Employees.GetStatusAsync(id));
        Assert.True((await Db.Punches.PunchAsync(id, "1234", PunchDirection.In)).Accepted);
    }

    [Fact]
    public async Task Stores_utc_with_the_site_offset()
    {
        // 2026-03-09 13:00Z is 09:00 in Toronto, one day after DST began (UTC-4).
        var toronto = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/Toronto");
        var clock = new ManualTimeProvider(Db.Clock.UtcNow, toronto);
        var punches = new PunchService(Db.Store, TestDatabase.FastHasher, clock);
        var id = await Db.AddEmployeeAsync();

        var punch = (await punches.PunchAsync(id, "1234", PunchDirection.In)).Punch!;

        Assert.Equal(-240, punch.UtcOffsetMinutes);
        Assert.Equal(new DateTime(2026, 3, 9, 9, 0, 0), punch.OccurredAtLocal.DateTime);
        Assert.Equal("2026-03-09T13:00:00.0000000Z",
            await Db.ScalarAsync<string>("SELECT occurred_at_utc FROM punch WHERE id = $id;", ("$id", punch.Id)));
    }

    [Fact]
    public async Task Punches_are_append_only()
    {
        var id = await Db.AddEmployeeAsync();
        var punch = (await Db.Punches.PunchAsync(id, "1234", PunchDirection.In)).Punch!;

        var update = await Assert.ThrowsAsync<SqliteException>(() =>
            Db.ExecuteAsync("UPDATE punch SET occurred_at_utc = '2020-01-01T00:00:00.0000000Z' WHERE id = $id;", ("$id", punch.Id)));
        var delete = await Assert.ThrowsAsync<SqliteException>(() =>
            Db.ExecuteAsync("DELETE FROM punch WHERE id = $id;", ("$id", punch.Id)));

        Assert.Contains("append-only", update.Message);
        Assert.Contains("append-only", delete.Message);
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
        var at = Db.Clock.UtcNow;

        await using (var uow = await Db.Store.BeginAsync())
        {
            await uow.AppendPunchAsync(new NewPunch(id, PunchDirection.In, at, 0, at, PunchSource.Kiosk));
        }

        Assert.Equal(0, await CountPunchesAsync(id));
    }

    private sealed class SlowPinHasher(IPinHasher inner) : IPinHasher
    {
        public string Hash(string pin) => inner.Hash(pin);

        public bool Verify(string pin, string encodedHash)
        {
            Thread.Sleep(50);
            return inner.Verify(pin, encodedHash);
        }
    }

    private Task<long> CountPunchesAsync(long employeeId) =>
        Db.ScalarAsync<long>("SELECT count(*) FROM punch WHERE employee_id = $id;", ("$id", employeeId));
}
