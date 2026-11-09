using PunchClock.SeedShifts;

namespace PunchClock.Tests;

public sealed class SeedShiftsTests : DatabaseTest
{
    private static readonly string Toronto = OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/Toronto";

    protected override string? SiteZone => Toronto;

    private static TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(Toronto);

    [Fact]
    public void An_employee_given_twice_is_a_usage_error()
    {
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse("--employee", "3", "--employee", "4", "--employee", "3"));
        Assert.Contains("--employee 3", ex.Message);
        Assert.Equal([3L, 4L], Options.Parse("--employee", "3", "--employee", "4").EmployeeIds);
    }

    [Fact]
    public void A_month_ends_at_site_midnight_not_a_day_later()
    {
        var october = new DateTime(2026, 10, 1);

        // 00:00:30 on November 1 in Toronto (EDT, UTC-4): October is over.
        Assert.True(Seeder.MonthHasEnded(october, new DateTimeOffset(2026, 11, 1, 4, 0, 30, TimeSpan.Zero), Zone));

        // Already November 1 in UTC but still 23:59 on October 31 at the site.
        Assert.False(Seeder.MonthHasEnded(october, new DateTimeOffset(2026, 11, 1, 3, 59, 0, TimeSpan.Zero), Zone));
    }

    [Fact]
    public async Task Seeds_a_past_month_as_audited_corrections_and_refuses_a_second_run()
    {
        await Db.AddAdminAsync();
        var ada = await Db.AddEmployeeAsync();
        var bob = await Db.AddEmployeeAsync("5678", "Bob", "Builder");
        var options = Options.Parse("--month", "2026-10");

        var plan = await Seeder.PlanAsync(Db.Store, options);

        // October 2026 has 22 weekdays.
        Assert.Equal(44, plan.Shifts.Count);
        Assert.Equal(22, plan.Shifts.Count(s => s.Employee.Id == ada));
        Assert.All(plan.Shifts, s => Assert.True(s.OutUtc > s.InUtc));
        Assert.Equal(88, await Seeder.ApplyAsync(Db.Store, plan));

        Assert.Equal(88, await Db.ScalarAsync<long>("SELECT count(*) FROM punch WHERE employee_id IN ($a, $b);", ("$a", ada), ("$b", bob)));
        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT count(*) FROM punch_exception_v;"));
        Assert.Empty(await Db.VerifyAsync());

        var again = await Assert.ThrowsAsync<SeedException>(() => Seeder.PlanAsync(Db.Store, options));
        Assert.Contains("already has", again.Message);
    }

    [Fact]
    public async Task A_plan_with_a_time_skipped_by_daylight_saving_writes_nothing()
    {
        await Db.AddAdminAsync();
        await Db.AddEmployeeAsync();

        // Clocks in Toronto went from 02:00 to 03:00 on Sunday 8 March 2026.
        var options = Options.Parse("--month", "2026-03", "--weekends", "--in", "02:00-02:30", "--apply");

        var ex = await Assert.ThrowsAsync<SeedException>(() => Seeder.PlanAsync(Db.Store, options));
        Assert.Contains("2026-03-08 02:", ex.Message);
        Assert.Contains("Nothing was written", ex.Message);
        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT count(*) FROM punch;"));
        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT count(*) FROM punch_correction;"));
    }

    [Fact]
    public async Task A_month_that_has_not_ended_at_the_site_is_refused()
    {
        await Db.AddAdminAsync();
        await Db.AddEmployeeAsync();
        var siteToday = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone).Date;
        var thisMonth = new DateTime(siteToday.Year, siteToday.Month, 1);

        var ex = await Assert.ThrowsAsync<SeedException>(() => Seeder.PlanAsync(Db.Store, Options.Parse("--month", $"{thisMonth:yyyy-MM}")));
        Assert.Equal(2, ex.ExitCode);
    }

    [Fact]
    public async Task The_csv_cannot_overwrite_the_database_or_any_other_database()
    {
        var db = Db.Database.Path;
        Assert.True(File.Exists(db));

        Assert.Throws<SeedException>(() => CsvTarget.EnsureNotDatabase(db, db));
        Assert.Throws<SeedException>(() => CsvTarget.EnsureNotDatabase(Path.Combine(Db.Folder, ".", Path.GetFileName(db)), db));
        Assert.Throws<SeedException>(() => CsvTarget.EnsureNotDatabase(db + "-wal", db));
        Assert.Throws<SeedException>(() => CsvTarget.EnsureNotDatabase(Db.Folder, db));

        var copy = Path.Combine(Db.Folder, "copy.csv");
        File.Copy(db, copy);
        Assert.Throws<SeedException>(() => CsvTarget.EnsureNotDatabase(copy, db));

        var csv = Path.Combine(Db.Folder, "plan.csv");
        CsvTarget.EnsureNotDatabase(csv, db);
        await File.WriteAllTextAsync(csv, "employee_id,employee\n");
        CsvTarget.EnsureNotDatabase(csv, db);
    }
}
