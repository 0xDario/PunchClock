using System.Globalization;

namespace PunchClock.Data.Sqlite;

/// <summary>Fixed-width UTC text format, so lexical order in SQLite equals time order.</summary>
internal static class SqliteTime
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string ToText(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string text) =>
        new(DateTime.ParseExact(text, Format, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
}
