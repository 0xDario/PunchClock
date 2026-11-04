using System.Globalization;
using PunchClock.Core.Reports.Pdf;

namespace PunchClock.Core.Reports;

/// <summary>Date and time formats of the printed shift detail, the old Access report's (9/8/2026 6:51:58 AM).</summary>
public static class ShiftDetailFormat
{
    public static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public const string Date = "M/d/yyyy";

    public static string Time(DateTime local) => local.ToString("M/d/yyyy h:mm:ss tt", Culture);

    public static string Hours(decimal hours) => hours.ToString("0.00", Culture);
}

/// <summary>
/// Prints a <see cref="ShiftDetailReport"/> the way the old Access pay report did: one block per
/// employee with the sum of hours beside the name, then one line per shift with time in, time out
/// and hours. Added to that: the weekday, a star on every time a manager entered, the note saying
/// who changed it and why, shifts that are not paid with the reason, a summary, and a footer that
/// sends corrections to the app and names the audit log entry the printout was made at.
/// </summary>
public static class ShiftDetailPdf
{
    private const double Left = 40;
    private const double Right = PdfDocument.PageWidth - 40;
    private const double DayX = Left + 6;
    private const double InX = 82;
    private const double OutX = 250;
    private const double HoursRight = Right - 6;
    private const double ContentTop = 100;
    private const double ContentBottom = 704;
    private const double RowHeight = 17;
    private const double NoteHeight = 10.5;
    private const double GroupHeight = 24;

    public static byte[] Render(ShiftDetailReport report)
    {
        var pdf = new PdfDocument();
        var y = NewPage(pdf, report);

        if (report.Employees.Count == 0)
        {
            pdf.Text(Left, y + 16, "No punches in this period.", PdfFont.Italic, 11, 0.3);
            y += 28;
        }

        foreach (var employee in report.Employees)
        {
            var rows = employee.Rows.Select(r => (Row: r, Notes: NoteLines(r))).ToList();
            if (y + GroupHeight + Height(rows[0].Notes) > ContentBottom)
            {
                y = NewPage(pdf, report);
            }

            y = GroupHeader(pdf, employee, y, continued: false);
            for (var i = 0; i < rows.Count; i++)
            {
                var (row, notes) = rows[i];
                if (y + Height(notes) > ContentBottom)
                {
                    y = GroupHeader(pdf, employee, NewPage(pdf, report), continued: true);
                }

                // A row with more notes than a page holds (never seen in practice) carries on
                // over the next pages; nothing is dropped.
                var printed = 0;
                do
                {
                    var room = Math.Max(1, (int)((ContentBottom - y - RowHeight - 3) / NoteHeight));
                    var chunk = notes.Skip(printed).Take(room).ToList();
                    y = Row(pdf, row, chunk, y, shaded: i % 2 == 1, continuation: printed > 0);
                    printed += chunk.Count;
                    if (printed < notes.Count)
                    {
                        y = GroupHeader(pdf, employee, NewPage(pdf, report), continued: true);
                    }
                }
                while (printed < notes.Count);
            }

            y += 10;
        }

        Summary(pdf, report, y);
        Footers(pdf, report);
        return pdf.Build(
            string.Create(CultureInfo.InvariantCulture, $"Hours Worked {report.From:yyyy-MM-dd} to {report.To:yyyy-MM-dd}"),
            report.GeneratedBy,
            report.GeneratedLocal);
    }

    private static double Height(IReadOnlyList<string> notes) => RowHeight + notes.Count * NoteHeight + (notes.Count > 0 ? 3 : 0);

    private static IReadOnlyList<string> NoteLines(ShiftDetailRow row) =>
        row.Notes.SelectMany(n => PdfDocument.Wrap(n, PdfFont.Italic, 8, HoursRight - InX)).ToList();

    private static double NewPage(PdfDocument pdf, ShiftDetailReport report)
    {
        pdf.AddPage();
        pdf.Text(Left, 54, "Hours Worked", PdfFont.Bold, 18);
        pdf.TextRight(Right, 54,
            $"Start Date: {report.From.ToString(ShiftDetailFormat.Date, ShiftDetailFormat.Culture)}     "
            + $"End Date: {report.To.ToString(ShiftDetailFormat.Date, ShiftDetailFormat.Culture)}",
            PdfFont.Bold, 11);
        pdf.Text(Left, 70, "Every shift that starts in the period, in the site's time. Hours count the minutes on the clock, as the old report did.",
            PdfFont.Regular, 8, 0.35);
        pdf.FillRect(Left, 78, Right - Left, 18, 0.85);
        pdf.Text(DayX, 90.5, "Day", PdfFont.Bold, 9);
        pdf.Text(InX, 90.5, "Time Punched In", PdfFont.Bold, 9);
        pdf.Text(OutX, 90.5, "Time Punched Out", PdfFont.Bold, 9);
        pdf.TextRight(HoursRight, 90.5, "Total Hours Worked", PdfFont.Bold, 9);
        return ContentTop;
    }

    private static double GroupHeader(PdfDocument pdf, ShiftDetailEmployee employee, double y, bool continued)
    {
        var name = employee.Name + (continued ? " (continued)" : "");
        var sum = $"Sum of Total Hours   {ShiftDetailFormat.Hours(employee.Hours)}";
        var sumWidth = PdfDocument.Width(sum, PdfFont.Bold, 10.5);
        pdf.Line(Left, y + 2, Right, y + 2, 0.75, 0.55);
        pdf.Text(DayX, y + 17, Fit(name, PdfFont.Bold, 12, HoursRight - sumWidth - DayX - 12), PdfFont.Bold, 12);
        pdf.TextRight(HoursRight, y + 17, sum, PdfFont.Bold, 10.5);
        return y + GroupHeight;
    }

    /// <param name="continuation">Only the rest of the notes of a row begun on an earlier page.</param>
    private static double Row(PdfDocument pdf, ShiftDetailRow row, IReadOnlyList<string> notes, double y, bool shaded, bool continuation)
    {
        var height = Height(notes);
        if (shaded)
        {
            pdf.FillRect(Left, y, Right - Left, height, 0.94);
        }

        var baseline = y + 12;
        if (continuation)
        {
            pdf.Text(InX, baseline, "Notes continued from the previous page", PdfFont.Italic, 9, 0.2);
            WriteNotes(pdf, notes, baseline);
            return y + height;
        }

        var removed = row.Kind is ShiftDetailKind.Voided or ShiftDetailKind.Moved;
        var gray = removed ? 0.45 : 0;
        if (row.SortTime != DateTime.MinValue)
        {
            pdf.Text(DayX, baseline, row.SortTime.ToString("ddd", ShiftDetailFormat.Culture), PdfFont.Regular, 10, gray);
        }

        if (row.InLocal is { } inLocal)
        {
            pdf.Text(InX, baseline, ShiftDetailFormat.Time(inLocal) + (row.InChanged ? " *" : ""), PdfFont.Regular, 10, gray);
        }

        pdf.Text(OutX, baseline, row.OutLocal is { } outLocal
                ? ShiftDetailFormat.Time(outLocal) + (row.OutChanged ? " *" : "")
                : row.Kind == ShiftDetailKind.NotCounted ? "no punch-out" : "",
            row.OutLocal is null ? PdfFont.Italic : PdfFont.Regular, 10, gray);

        if (row.Hours is { } hours)
        {
            pdf.TextRight(HoursRight, baseline, ShiftDetailFormat.Hours(hours), PdfFont.Regular, 10);
        }
        else
        {
            pdf.TextRight(HoursRight, baseline, row.Kind switch
            {
                ShiftDetailKind.NotCounted => "not counted",
                ShiftDetailKind.Voided => "voided",
                ShiftDetailKind.Moved => "moved",
                _ => "no punch-in",
            }, PdfFont.Italic, 9, 0.2);
        }

        WriteNotes(pdf, notes, baseline);
        return y + height;
    }

    private static void WriteNotes(PdfDocument pdf, IReadOnlyList<string> notes, double baseline)
    {
        for (var i = 0; i < notes.Count; i++)
        {
            pdf.Text(InX, baseline + 11 + i * NoteHeight, notes[i], PdfFont.Italic, 8, 0.25);
        }
    }

    private static void Summary(PdfDocument pdf, ShiftDetailReport report, double y)
    {
        const double shiftsRight = 400;
        const double notCountedRight = 480;
        double Header(double top)
        {
            pdf.Text(Left, top + 16, "Summary", PdfFont.Bold, 12);
            pdf.FillRect(Left, top + 24, Right - Left, 16, 0.85);
            pdf.Text(DayX, top + 35.5, "Employee", PdfFont.Bold, 9);
            pdf.TextRight(shiftsRight, top + 35.5, "Shifts counted", PdfFont.Bold, 9);
            pdf.TextRight(notCountedRight, top + 35.5, "Not counted", PdfFont.Bold, 9);
            pdf.TextRight(HoursRight, top + 35.5, "Total Hours", PdfFont.Bold, 9);
            return top + 42;
        }

        y = y + 42 + 2 * RowHeight > ContentBottom ? Header(NewPage(pdf, report)) : Header(y + 4);
        foreach (var (employee, i) in report.Employees.Select((e, i) => (e, i)))
        {
            if (y + RowHeight > ContentBottom)
            {
                y = Header(NewPage(pdf, report));
            }

            if (i % 2 == 1)
            {
                pdf.FillRect(Left, y, Right - Left, RowHeight, 0.94);
            }

            pdf.Text(DayX, y + 12, Fit(employee.Name, PdfFont.Regular, 10, shiftsRight - 80 - DayX), PdfFont.Regular, 10);
            pdf.TextRight(shiftsRight, y + 12, employee.Shifts.ToString(CultureInfo.InvariantCulture), PdfFont.Regular, 10);
            pdf.TextRight(notCountedRight, y + 12, employee.NotCounted.ToString(CultureInfo.InvariantCulture), PdfFont.Regular, 10);
            pdf.TextRight(HoursRight, y + 12, ShiftDetailFormat.Hours(employee.Hours), PdfFont.Regular, 10);
            y += RowHeight;
        }

        if (y + RowHeight + 4 > ContentBottom)
        {
            y = Header(NewPage(pdf, report));
        }

        pdf.Line(Left, y + 1, Right, y + 1, 0.75, 0);
        pdf.Text(DayX, y + 14, "All employees", PdfFont.Bold, 10);
        pdf.TextRight(shiftsRight, y + 14, report.TotalShifts.ToString(CultureInfo.InvariantCulture), PdfFont.Bold, 10);
        pdf.TextRight(notCountedRight, y + 14, report.Employees.Sum(e => e.NotCounted).ToString(CultureInfo.InvariantCulture), PdfFont.Bold, 10);
        pdf.TextRight(HoursRight, y + 14, ShiftDetailFormat.Hours(PayRules.Hours(report.TotalMinutes)), PdfFont.Bold, 10);
    }

    private static void Footers(PdfDocument pdf, ShiftDetailReport report)
    {
        var printed = $"Printed {report.GeneratedLocal.ToString("MMMM d, yyyy h:mm tt", ShiftDetailFormat.Culture)} by {report.GeneratedBy}";
        var anchor = report.AuditSeq > 0
            ? $"Audit log entry {report.AuditSeq.ToString(CultureInfo.InvariantCulture)}, {report.AuditHash[..Math.Min(16, report.AuditHash.Length)]}"
            : "";
        for (var page = 0; page < pdf.PageCount; page++)
        {
            pdf.Line(Left, 714, Right, 714, 0.5, 0.55, page);
            pdf.Text(Left, 726, printed, PdfFont.Regular, 8, 0.2, page);
            pdf.TextRight(Right, 726, $"Page {page + 1} of {pdf.PageCount}", PdfFont.Regular, 8, 0.2, page);
            pdf.Text(Left, 738,
                "* A manager added or changed this time in PunchClock. The note under the line says who, when and why.",
                PdfFont.Italic, 8, 0.2, page);
            pdf.Text(Left, 750,
                "Wrong or missing time? Correct it in PunchClock (Admin, Corrections tab), then print again. Changes made on paper are not recorded.",
                PdfFont.Italic, 8, 0.2, page);
            pdf.TextRight(Right, 762, anchor, PdfFont.Regular, 7, 0.45, page);
        }
    }

    /// <summary>The text, cut with "..." if it is wider than <paramref name="width"/>.</summary>
    private static string Fit(string text, PdfFont font, double size, double width)
    {
        if (PdfDocument.Width(text, font, size) <= width)
        {
            return text;
        }

        var cut = text.Length;
        while (cut > 1 && PdfDocument.Width(text[..cut] + "...", font, size) > width)
        {
            cut--;
        }

        return text[..cut].TrimEnd() + "...";
    }
}
