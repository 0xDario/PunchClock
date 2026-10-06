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
    /// The zone to show site times in: the site's configured zone, or the machine's own zone while
    /// none is configured (or the configured id is unknown on this machine). Punching itself has no
    /// fallback: it is refused until the site zone is set.
    /// </summary>
    public static TimeZoneInfo ResolveZone(string? timeZoneId, TimeZoneInfo fallback)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId == SiteSettingKeys.Unset)
        {
            return fallback;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone) ? zone : fallback;
    }

    /// <summary>
    /// UTC bounds covering the site dates <paramref name="fromDate"/> to <paramref name="toDate"/>
    /// (inclusive) in any zone, with a day of margin each side for the caller to trim. Only the
    /// calendar date is used, whatever the <see cref="DateTime.Kind"/> (a date picker's is Local).
    /// </summary>
    public static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) CoveringUtcRange(DateTime fromDate, DateTime toDate) =>
        (new DateTimeOffset(DateTime.SpecifyKind(fromDate.Date.AddDays(-1), DateTimeKind.Unspecified), TimeSpan.Zero),
         new DateTimeOffset(DateTime.SpecifyKind(toDate.Date.AddDays(2), DateTimeKind.Unspecified), TimeSpan.Zero));
}
