using Microsoft.Data.Sqlite;
using PunchClock.Core.Site;

namespace PunchClock.Tests;

public sealed class SiteSettingsTests : DatabaseTest
{
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
