namespace PunchClock.Core.Site;

public static class SiteSettingKeys
{
    /// <summary>Windows time zone id of the site; <see cref="Unset"/> until an admin picks one.</summary>
    public const string TimeZoneId = "time_zone_id";

    public const string Unset = "UNSET";
}

public static class SiteTime
{
    /// <summary>
    /// The zone punch offsets are computed in: the site's configured zone, or the machine's own
    /// zone while none is configured (or the configured id is unknown on this machine).
    /// </summary>
    public static TimeZoneInfo ResolveZone(string? timeZoneId, TimeZoneInfo fallback)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId == SiteSettingKeys.Unset)
        {
            return fallback;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone) ? zone : fallback;
    }
}
