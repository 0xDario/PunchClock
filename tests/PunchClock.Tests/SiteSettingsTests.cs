using Microsoft.Data.Sqlite;
using PunchClock.Core.Audit;
using PunchClock.Core.Punches;
using PunchClock.Core.Site;

namespace PunchClock.Tests;

public sealed class SiteSettingsTests : DatabaseTest
{
    // A fresh install: the zone is UNSET until an admin chooses one.
    protected override string? SiteZone => null;

    [Fact]
    public async Task Punches_are_refused_until_an_admin_sets_the_zone()
    {
        var admin = await Db.AddAdminAsync();
        var id = await Db.AddEmployeeAsync();

        Assert.Equal(PunchRejection.SiteTimeZoneNotSet, (await Db.Punches.PunchAsync(id, "1234", PunchDirection.In)).Rejection);
        var ex = await Assert.ThrowsAsync<SqliteException>(() => Db.ExecuteAsync(AuditActor.ForEmployee(id), """
            INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source)
            VALUES ($id, 'IN', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 0, 'kiosk');
            """, ("$id", id)));
        Assert.Contains("site time zone is not configured", ex.Message);

        await Db.Site.SetTimeZoneAsync(admin, TimeZoneInfo.Utc.Id);
        Assert.True((await Db.Punches.PunchAsync(id, "1234", PunchDirection.In)).Accepted);
    }

    [Fact]
    public async Task Database_rejects_an_unknown_zone_even_from_an_admin_connection()
    {
        var admin = await Db.AddAdminAsync();

        var ex = await Assert.ThrowsAsync<SqliteException>(() => Db.ExecuteAsync(AuditActor.ForUser(admin.Id),
            "UPDATE site_setting SET value = 'Mars/Olympus_Mons' WHERE key = 'time_zone_id';"));

        Assert.Contains("known Windows time zone", ex.Message);
    }

    [Theory]
    [InlineData("UTC", "2026-07-01T12:00:00.000Z", 0)]
    [InlineData("Eastern Standard Time", "2026-07-01T12:00:00.000Z", -240)]
    [InlineData("Eastern Standard Time", "2026-01-15T12:00:00.000Z", -300)]
    [InlineData("Tokyo Standard Time", "2026-01-15T12:00:00.000Z", 540)]
    public async Task Offset_function_uses_windows_zone_rules(string zone, string utc, long expected)
    {
        Assert.Equal(expected, await Db.ScalarAsync<long>("SELECT pc_utc_offset($zone, $utc);", ("$zone", zone), ("$utc", utc)));
    }

    [Fact]
    public async Task Offset_function_returns_null_instead_of_failing()
    {
        Assert.Equal(1, await Db.ScalarAsync<long>("""
            SELECT pc_utc_offset('Mars/Olympus_Mons', '2026-01-01T00:00:00.000Z') IS NULL
               AND pc_utc_offset(NULL, '2026-01-01T00:00:00.000Z') IS NULL
               AND pc_utc_offset('UTC', NULL) IS NULL
               AND pc_utc_offset('UTC', 'not a time') IS NULL;
            """));
    }

    [Fact]
    public async Task Time_zone_starts_unset_and_admin_sets_it()
    {
        var admin = await Db.AddAdminAsync();
        var zone = TimeZoneInfo.GetSystemTimeZones()[0].Id;

        Assert.Null(await Db.Site.GetTimeZoneIdAsync());
        await Db.Site.SetTimeZoneAsync(admin, zone);

        Assert.Equal(zone, await Db.Site.GetTimeZoneIdAsync());
        Assert.Contains("UNSET", await Db.ScalarAsync<string>(
            "SELECT before_json FROM audit_log WHERE table_name = 'site_setting' AND action = 'UPDATE';"));
    }

    [Fact]
    public async Task Unknown_zone_is_rejected()
    {
        var admin = await Db.AddAdminAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => Db.Site.SetTimeZoneAsync(admin, "Mars/Olympus_Mons"));
        Assert.Null(await Db.Site.GetTimeZoneIdAsync());
    }

    [Fact]
    public async Task Managers_cannot_set_the_time_zone()
    {
        var manager = await Db.AddManagerAsync();

        var ex = await Assert.ThrowsAsync<SqliteException>(() => Db.Site.SetTimeZoneAsync(manager, TimeZoneInfo.Utc.Id));

        Assert.Contains("admin only", ex.Message);
    }

    [Fact]
    public void Resolve_zone_falls_back_while_unset_or_unknown()
    {
        var fallback = TimeZoneInfo.Utc;

        Assert.Same(fallback, SiteTime.ResolveZone(null, fallback));
        Assert.Same(fallback, SiteTime.ResolveZone(SiteSettingKeys.Unset, fallback));
        Assert.Same(fallback, SiteTime.ResolveZone("Not/AZone", fallback));
        Assert.NotSame(fallback, SiteTime.ResolveZone(TimeZoneInfo.GetSystemTimeZones()[0].Id, fallback));
    }
}
