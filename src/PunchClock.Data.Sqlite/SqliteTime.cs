using System.Globalization;

namespace PunchClock.Data.Sqlite;

/// <summary>
/// The schema's timestamp format, <c>YYYY-MM-DDTHH:MM:SS.sssZ</c>: UTC, millisecond precision,
/// fixed width so text order is time order. CHECK constraints reject anything else.
/// </summary>
public static class SqliteTime
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Formats in UTC; sub-millisecond ticks are truncated.</summary>
    public static string ToText(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string text) =>
        new(DateTime.ParseExact(text, Format, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

    /// <summary>The instant as stored: truncated to whole milliseconds.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
}
