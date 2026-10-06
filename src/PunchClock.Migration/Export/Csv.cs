using System.Text;

namespace PunchClock.Migration.Export;

/// <summary>
/// RFC 4180 reader for the legacy exporter's CSVs. An empty unquoted field is
/// NULL (returned as <c>null</c>); a quoted empty field <c>""</c> is the empty
/// string. The exporter writes both, so the distinction must survive.
/// </summary>
internal static class Csv
{
    public static List<string?[]> Parse(string text, string fileName)
    {
        var rows = new List<string?[]>();
        var fields = new List<string?>();
        var field = new StringBuilder();
        var quoted = false;
        var inQuotes = false;
        var line = 1;
        var i = 0;

        if (text.Length > 0 && text[0] == '﻿')
            i = 1;

        void EndField()
        {
            fields.Add(field.Length == 0 && !quoted ? null : field.ToString());
            field.Clear();
            quoted = false;
        }

        void EndRow()
        {
            EndField();
            rows.Add(fields.ToArray());
            fields.Clear();
        }

        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (c == '\n')
                        line++;
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0 && !quoted:
                    inQuotes = true;
                    quoted = true;
                    break;
                case '"':
                    throw new ExportFormatException($"{fileName} line {line}: stray quote inside an unquoted field.");
                case ',':
                    EndField();
                    break;
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    break;
                case '\n':
                    EndRow();
                    line++;
                    break;
                default:
                    if (quoted)
                        throw new ExportFormatException($"{fileName} line {line}: text after a closing quote.");
                    field.Append(c);
                    break;
            }
        }

        if (inQuotes)
            throw new ExportFormatException($"{fileName}: file ends inside a quoted field.");

        // Last line without a terminating newline.
        if (field.Length > 0 || quoted || fields.Count > 0)
            EndRow();

        return rows;
    }
}
