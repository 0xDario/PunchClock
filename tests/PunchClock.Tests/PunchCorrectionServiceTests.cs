using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;
using PunchClock.Core.Punches;

namespace PunchClock.Tests;

public sealed class PunchCorrectionServiceTests : DatabaseTest
{
    private static readonly string Toronto = OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/Toronto";

    protected override string? SiteZone => Toronto;

    private static TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(Toronto);

    private DateTime LocalNow(TimeSpan ago) => TimeZoneInfo.ConvertTime(Db.Clock.GetUtcNow() - ago, Zone).DateTime;

    [Fact]
    public async Task Manager_adds_a_forgotten_punch_out_with_a_reason()
    {
        var manager = await Db.AddManagerAsync();
        var id = await Db.AddEmployeeAsync();
        await Db.Punches.PunchAsync(id, "1234", PunchDirection.In);
        Db.Clock.Advance(TimeSpan.FromSeconds(10));
        var outAt = LocalNow(TimeSpan.Zero);

        Assert.Equal(CorrectionResult.Corrected,
            await Db.Corrections.AddAsync(manager, id, PunchDirection.Out, outAt, "Forgot to punch out at end of shift"));

        Assert.Equal(ClockStatus.Out, await Db.Employees.GetStatusAsync(id));
        var added = (await Db.Corrections.ListAsync(id, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)))
            .Single(r => r.Punch.Source == PunchSource.Correction).Punch;
        Assert.Equal(outAt.AddTicks(-(outAt.Ticks % TimeSpan.TicksPerMillisecond)), added.OccurredAtLocal.DateTime);
        Assert.Equal((int)Zone.GetUtcOffset(added.OccurredAtUtc).TotalMinutes, added.UtcOffsetMinutes);
        Assert.Equal("Forgot to punch out at end of shift", await Db.ScalarAsync<string>(
            "SELECT reason FROM audit_log WHERE table_name = 'punch_correction' AND actor_id = $by;", ("$by", manager.Id)));
        Assert.Empty(await Db.VerifyAsync());
    }

    [Fact]
    public async Task Adjust_and_void_supersede_the_punch_and_keep_it_in_history()
    {
        var manager = await Db.AddManagerAsync();
        var id = await Db.AddEmployeeAsync();
        var original = (await Db.Punches.PunchAsync(id, "1234", PunchDirection.In)).Punch!;

        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.AdjustAsync(
            manager, original.Id, PunchDirection.In, LocalNow(TimeSpan.FromMinutes(20)), "Was on site 20 minutes earlier"));
        Assert.Equal(CorrectionResult.PunchNotFound, await Db.Corrections.VoidAsync(manager, original.Id, "Already superseded punch"));

        var replacement = (await Db.Corrections.ListAsync(id, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1))).Single().Punch;
        Assert.Equal(PunchSource.Correction, replacement.Source);
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.VoidAsync(manager, replacement.Id, "Was not working that day"));

        Assert.Empty(await Db.Corrections.ListAsync(id, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Equal(2, await Db.ScalarAsync<long>("SELECT count(*) FROM punch WHERE employee_id = $id;", ("$id", id)));
        Assert.Empty(await Db.VerifyAsync());
    }

    [Fact]
    public async Task Corrections_are_refused_without_a_reason_in_the_future_or_on_own_punches()
    {
        var admin = await Db.AddAdminAsync();
        var manager = await Db.AddManagerAsync();
        var id = await Db.AddEmployeeAsync();
        await Db.ExecuteAsync(AuditActor.ForUser(admin.Id),
            "UPDATE app_user SET employee_id = $e WHERE id = $m;", ("$e", id), ("$m", manager.Id));
        var linked = manager with { EmployeeId = id };
        var other = await Db.AddEmployeeAsync(first: "Other");
        var earlier = LocalNow(TimeSpan.FromHours(1));

        Assert.Equal(CorrectionResult.ReasonTooShort, await Db.Corrections.AddAsync(admin, other, PunchDirection.In, earlier, "fix"));
        Assert.Equal(CorrectionResult.InFuture,
            await Db.Corrections.AddAsync(admin, other, PunchDirection.In, LocalNow(TimeSpan.FromHours(-1)), "Shift starts later today"));
        Assert.Equal(CorrectionResult.OwnPunches, await Db.Corrections.AddAsync(linked, id, PunchDirection.In, earlier, "My own missed punch-in"));
        Assert.Equal(CorrectionResult.NotAllowed, await Db.Corrections.AddAsync(
            admin with { Role = UserRole.Migration }, other, PunchDirection.In, earlier, "Not a manager account"));
        Assert.Equal(CorrectionResult.EmployeeNotFound, await Db.Corrections.AddAsync(admin, 999, PunchDirection.In, earlier, "No such employee here"));
        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT count(*) FROM punch_correction;"));
    }

    [Fact]
    public void Local_times_skipped_by_dst_are_invalid_and_repeated_ones_take_the_first_occurrence()
    {
        Assert.Null(PunchCorrectionService.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0), Zone));
        Assert.Equal(new DateTimeOffset(2025, 11, 2, 5, 30, 0, TimeSpan.Zero),
            PunchCorrectionService.ToUtc(new DateTime(2025, 11, 2, 1, 30, 0), Zone));
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 13, 0, 0, TimeSpan.Zero),
            PunchCorrectionService.ToUtc(new DateTime(2026, 7, 1, 9, 0, 0), Zone));
    }

    [Fact]
    public async Task Review_list_shows_the_issue_on_each_punch()
    {
        var manager = await Db.AddManagerAsync();
        var id = await Db.AddEmployeeAsync();
        // Two INs in a row: the first one's shift has no punch-out.
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.AddAsync(
            manager, id, PunchDirection.In, LocalNow(TimeSpan.FromHours(30)), "Shift from the import report"));
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.AddAsync(
            manager, id, PunchDirection.In, LocalNow(TimeSpan.FromHours(2)), "Punch-in written on paper"));

        var rows = await Db.Corrections.ListAsync(id, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(2, rows.Count);
        Assert.Equal("missing_out", rows[0].Issue);
        Assert.Null(rows[1].Issue);
    }
}
