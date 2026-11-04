using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PunchClock.Core.Reports.Pdf;

public enum PdfFont
{
    Regular,
    Bold,
    Italic,
}

/// <summary>
/// A small PDF 1.4 writer for printed reports: text in the standard Helvetica fonts, filled
/// rectangles and lines on US Letter pages. Coordinates are points from the top-left corner,
/// as a page is read; the writer flips them to PDF's bottom-left origin. Output depends only
/// on what was drawn and the given dates, so the same report gives the same bytes.
/// </summary>
internal sealed class PdfDocument
{
    public const double PageWidth = 612;
    public const double PageHeight = 792;

    private readonly List<StringBuilder> _pages = [];

    public int PageCount => _pages.Count;

    public void AddPage() => _pages.Add(new StringBuilder());

    private StringBuilder Page => _pages.Count > 0 ? _pages[^1] : throw new InvalidOperationException("Add a page first.");

    public static double Width(string text, PdfFont font, double size) => HelveticaMetrics.Width(Clean(text), font == PdfFont.Bold, size);

    /// <summary>Text with its baseline at <paramref name="y"/>; <paramref name="gray"/> 0 is black, 1 is white.</summary>
    public void Text(double x, double y, string text, PdfFont font, double size, double gray = 0, int page = -1)
    {
        var target = page < 0 ? Page : _pages[page];
        var name = font switch { PdfFont.Bold => "F2", PdfFont.Italic => "F3", _ => "F1" };
        target.Append(CultureInfo.InvariantCulture, $"BT /{name} {N(size)} Tf {N(gray)} g {N(x)} {N(PageHeight - y)} Td ");
        AppendString(target, Clean(text));
        target.Append(" Tj ET\n");
    }

    public void TextRight(double right, double y, string text, PdfFont font, double size, double gray = 0, int page = -1) =>
        Text(right - Width(text, font, size), y, text, font, size, gray, page);

    /// <summary>A filled rectangle whose top-left corner is (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void FillRect(double x, double y, double width, double height, double gray) =>
        Page.Append(CultureInfo.InvariantCulture, $"{N(gray)} g {N(x)} {N(PageHeight - y - height)} {N(width)} {N(height)} re f\n");

    public void Line(double x1, double y1, double x2, double y2, double width, double gray, int page = -1)
    {
        var target = page < 0 ? Page : _pages[page];
        target.Append(CultureInfo.InvariantCulture, $"{N(gray)} G {N(width)} w {N(x1)} {N(PageHeight - y1)} m {N(x2)} {N(PageHeight - y2)} l S\n");
    }

    /// <summary>Splits <paramref name="text"/> into lines no wider than <paramref name="maxWidth"/>, breaking at spaces where it can.</summary>
    public static IReadOnlyList<string> Wrap(string text, PdfFont font, double size, double maxWidth)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in Clean(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (Width(candidate, font, size) <= maxWidth)
            {
                line.Clear().Append(candidate);
                continue;
            }

            if (line.Length > 0)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            // A single word wider than the line (a long username, say) is cut, never lost.
            var rest = word;
            while (Width(rest, font, size) > maxWidth)
            {
                var take = rest.Length - 1;
                while (take > 1 && Width(rest[..take], font, size) > maxWidth)
                {
                    take--;
                }

                lines.Add(rest[..take]);
                rest = rest[take..];
            }

            line.Append(rest);
        }

        if (line.Length > 0 || lines.Count == 0)
        {
            lines.Add(line.ToString());
        }

        return lines;
    }

    public byte[] Build(string title, string author, DateTimeOffset created)
    {
        if (_pages.Count == 0)
        {
            AddPage();
        }

        // Objects: 1 catalog, 2 page tree, 3-5 fonts, 6 info, then a page and its content per page.
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Pages /Count {_pages.Count} /Kids [{string.Join(' ', Enumerable.Range(0, _pages.Count).Select(i => $"{7 + 2 * i} 0 R"))}] >>")),
            Font("Helvetica"),
            Font("Helvetica-Bold"),
            Font("Helvetica-Oblique"),
            Info(title, author, created),
        };

        foreach (var (page, i) in _pages.Select((p, i) => (p, i)))
        {
            objects.Add(Ascii(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] "
                + $"/Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R >> >> /Contents {8 + 2 * i} 0 R >>")));
            objects.Add(Stream(Latin1(page.ToString())));
        }

        using var output = new MemoryStream();
        Write(output, Ascii("%PDF-1.4\n%âãÏÓ\n", latin1: true));
        var offsets = new List<long>();
        foreach (var (body, i) in objects.Select((b, i) => (b, i)))
        {
            offsets.Add(output.Position);
            Write(output, Ascii(string.Create(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n")));
            Write(output, body);
            Write(output, Ascii("\nendobj\n"));
        }

        var xref = output.Position;
        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 6 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(output, Ascii(table.ToString()));
        return output.ToArray();
    }

    private static byte[] Font(string baseFont) =>
        Ascii($"<< /Type /Font /Subtype /Type1 /BaseFont /{baseFont} /Encoding /WinAnsiEncoding >>");

    private static byte[] Info(string title, string author, DateTimeOffset created)
    {
        var offset = created.Offset;
        var date = string.Create(CultureInfo.InvariantCulture,
            $"D:{created:yyyyMMddHHmmss}{(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):D2}'{Math.Abs(offset.Minutes):D2}'");
        var info = new StringBuilder("<< /Title ");
        AppendString(info, Clean(title));
        info.Append(" /Author ");
        AppendString(info, Clean(author));
        info.Append(" /Creator (PunchClock) /Producer (PunchClock) /CreationDate ");
        AppendString(info, date);
        info.Append(" >>");
        return Latin1(info.ToString());
    }

    private static byte[] Stream(byte[] content)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(content);
        }

        var header = Ascii(string.Create(CultureInfo.InvariantCulture, $"<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n"));
        return [.. header, .. compressed.ToArray(), .. Ascii("\nendstream")];
    }

    /// <summary>
    /// A PDF literal string in WinAnsi bytes, carried as Latin-1 chars until the page is encoded.
    /// Bytes outside printable ASCII are octal escapes, so the content stream stays plain text.
    /// </summary>
    private static void AppendString(StringBuilder target, string text)
    {
        target.Append('(');
        foreach (var b in HelveticaMetrics.Encode(text))
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    target.Append('\\').Append((char)b);
                    break;
                case < 32 or > 126:
                    target.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    break;
                default:
                    target.Append((char)b);
                    break;
            }
        }

        target.Append(')');
    }

    /// <summary>One line of text the standard fonts can show: composed accents, no control characters.</summary>
    private static string Clean(string text)
    {
        var composed = text.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(composed.Length);
        foreach (var c in composed)
        {
            builder.Append(char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c);
        }

        return builder.ToString();
    }

    private static string N(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static byte[] Ascii(string text, bool latin1 = false) => latin1 ? Latin1(text) : Encoding.ASCII.GetBytes(text);

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static void Write(Stream stream, byte[] bytes) => stream.Write(bytes);
}
