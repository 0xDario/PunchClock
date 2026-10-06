using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;
using PunchClock.Core.Persistence;

namespace PunchClock.Core.Site;

public sealed class SiteSettingsService(IPunchClockStore store)
{
    /// <returns>The configured Windows time zone id, or null while unset.</returns>
    public async Task<string?> GetTimeZoneIdAsync(CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);
        var value = await uow.GetSettingAsync(SiteSettingKeys.TimeZoneId, ct);
        return value is null or SiteSettingKeys.Unset ? null : value;
    }

    /// <summary>Sets the site zone used for new punch offsets. Existing punches keep the offset they were recorded with.</summary>
    /// <exception cref="ArgumentException">The id is not a time zone known to this machine.</exception>
    public async Task SetTimeZoneAsync(AppUser admin, string timeZoneId, CancellationToken ct = default)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
        {
            throw new ArgumentException($"Unknown time zone '{timeZoneId}'.", nameof(timeZoneId));
        }

        await using var uow = await store.BeginAsync(ct);
        uow.ActAs(AuditActor.ForUser(admin.Id));
        await uow.SetSettingAsync(SiteSettingKeys.TimeZoneId, timeZoneId, ct);
        await uow.CommitAsync(ct);
    }
}
