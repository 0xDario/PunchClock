using System.Globalization;
using System.Text;

namespace PunchClock.Core.Reports;

/// <summary>
/// RFC 4180 CSV for spreadsheets and payroll imports: UTF-8 with a byte-order mark (so Excel
/// reads names correctly), CRLF line endings, quotes only where needed. Text a person typed
/// (names, reasons) that starts with = + - @ or a control character is prefixed with an
/// apostrophe, so a spreadsheet shows it instead of running it as a formula.
/// </summary>
public static class Csv
{
    public static byte[] Build(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<object?>> rows, out int rowCount)
    {
        var text = new StringBuilder();
        AppendLine(text, header.Cast<object?>().ToList());
        rowCount = 0;
        foreach (var row in rows)
        {
            AppendLine(text, row);
            rowCount++;
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    public static string LocalTime(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string UtcTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void AppendLine(StringBuilder text, IReadOnlyList<object?> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append(Field(fields[i]));
        }

        text.Append("\r\n");
    }

    internal static string Field(object? value)
    {
        var text = value switch
        {
            null => "",
            string s => Defuse(s),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => Defuse(other.ToString() ?? ""),
        };

        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 || text != text.Trim()
            ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : text;
    }

    private static string Defuse(string text) =>
        text.Length > 0 && (text[0] is '=' or '+' or '-' or '@' || char.IsControl(text[0])) ? "'" + text : text;
}
