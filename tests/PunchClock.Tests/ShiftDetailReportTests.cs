using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PunchClock.Core.Accounts;
using PunchClock.Core.Punches;
using PunchClock.Core.Reports;
using PunchClock.Core.Reports.Pdf;

namespace PunchClock.Tests;

public sealed partial class ShiftDetailReportTests : DatabaseTest
{
    private static readonly string Toronto = OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/Toronto";

    private static readonly DateOnly From = new(2026, 9, 6);

    private static readonly DateOnly To = new(2026, 9, 19);

    private static TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(Toronto);

    protected override string? SiteZone => Toronto;

    private AppUser _manager = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _manager = await Db.AddManagerAsync();
    }

    private async Task<long> Add(long employeeId, PunchDirection direction, DateTime local, string reason = "Entered from the paper log")
    {
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.AddAsync(_manager, employeeId, direction, local, reason));
        return await Db.ScalarAsync<long>("SELECT max(id) FROM punch;");
    }

    private async Task<(long In, long? Out)> Shift(long employeeId, DateTime inLocal, DateTime? outLocal)
    {
        var punchIn = await Add(employeeId, PunchDirection.In, inLocal);
        return (punchIn, outLocal is { } o ? await Add(employeeId, PunchDirection.Out, o) : null);
    }

    private async Task<long> NewestPunch() => await Db.ScalarAsync<long>("SELECT max(id) FROM punch;");

    [Fact]
    public async Task Lists_every_shift_with_the_pay_reports_totals()
    {
        var meffe = await Db.AddEmployeeAsync(first: "Antonietta", last: "Meffe");
        var bignucolo = await Db.AddEmployeeAsync(first: "Natalie", last: "Bignucolo");
        // Shifts from the old report's printout; its sum for this employee was 46.77.
        (string In, string Out)[] old =
        [
            ("9/8/2026 6:51:58 AM", "9/8/2026 1:33:29 PM"), ("9/9/2026 6:52:27 AM", "9/9/2026 1:09:11 PM"),
            ("9/10/2026 5:54:18 AM", "9/10/2026 1:03:46 PM"), ("9/14/2026 5:19:47 AM", "9/14/2026 9:44:11 AM"),
            ("9/15/2026 5:49:05 AM", "9/15/2026 1:32:18 PM"), ("9/16/2026 5:55:26 AM", "9/16/2026 1:17:41 PM"),
            ("9/17/2026 5:53:10 AM", "9/17/2026 1:01:33 PM"),
        ];
        foreach (var (i, o) in old)
        {
            await Shift(meffe, Parse(i), Parse(o));
        }

        await Shift(bignucolo, new(2026, 9, 5, 22, 0, 0), new(2026, 9, 6, 6, 0, 0));    // starts before the period
        await Shift(bignucolo, new(2026, 9, 8, 17, 9, 18), new(2026, 9, 8, 17, 39, 11)); // 0.50
        await Shift(bignucolo, new(2026, 9, 19, 22, 0, 0), new(2026, 9, 20, 6, 0, 0));  // ends after: not counted
        await Shift(bignucolo, new(2026, 9, 12, 5, 55, 0), null);                       // no punch-out: not counted

        var report = await Db.Reports.ShiftDetailReportAsync(_manager, From, To);
        var pay = await Db.Reports.PayReportAsync(_manager, From, To);

        Assert.Equal(["Natalie Bignucolo", "Antonietta Meffe"], report.Employees.Select(e => e.Name));
        var m = report.Employees[1];
        Assert.Equal((7, 46.77m), (m.Shifts, m.Hours));
        Assert.Equal([6.70m, 6.28m, 7.15m, 4.42m, 7.72m, 7.37m, 7.13m], m.Rows.Select(r => r.Hours!.Value));
        Assert.Equal(Parse("9/8/2026 6:51:58 AM"), m.Rows[0].InLocal);

        var b = report.Employees[0];
        Assert.Equal(
            [ShiftDetailKind.Counted, ShiftDetailKind.NotCounted, ShiftDetailKind.NotCounted],
            b.Rows.Select(r => r.Kind));
        Assert.Equal((1, 30L, 2), (b.Shifts, b.Minutes, b.NotCounted));
        Assert.Contains("Not counted. No punch-out", b.Rows[1].Notes[^1], StringComparison.Ordinal);
        Assert.Contains("Not counted. Ends after the period", b.Rows[2].Notes[^1], StringComparison.Ordinal);

        // Same figures as the pay report, employee by employee.
        Assert.Equal(
            pay.Lines.Select(l => (l.EmployeeId, l.Shifts, l.Minutes, l.NotCounted)),
            report.Employees.Select(e => (e.EmployeeId, e.Shifts, e.Minutes, e.NotCounted)));
        Assert.Equal(pay.TotalMinutes, report.TotalMinutes);
    }

    [Fact]
    public async Task Each_manager_change_says_who_when_and_why()
    {
        var ada = await Db.AddEmployeeAsync();
        var (_, punchOut) = await Shift(ada, new(2026, 9, 8, 7, 50, 41), new(2026, 9, 8, 17, 12, 3));
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.AdjustAsync(
            _manager, punchOut!.Value, PunchDirection.Out, new DateTime(2026, 9, 8, 15, 16, 2), "Left at 3:16, not 5:12"));
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.AdjustAsync(
            _manager, await NewestPunch(), PunchDirection.Out, new DateTime(2026, 9, 8, 15, 20, 0), "Checked the camera: 3:20"));

        var row = (await Db.Reports.ShiftDetailReportAsync(_manager, From, To)).Employees.Single().Rows.Single();
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone).ToString("M/d/yyyy", ShiftDetailFormat.Culture);

        Assert.Equal((ShiftDetailKind.Counted, true, true), (row.Kind, row.InChanged, row.OutChanged));
        Assert.Equal(new DateTime(2026, 9, 8, 15, 20, 0), row.OutLocal);
        Assert.Equal(
        [
            $"In added by Manager on {today}: Entered from the paper log",
            $"Out added by Manager on {today}: Entered from the paper log",
            $"Out changed from 9/8/2026 5:12:03 PM to 9/8/2026 3:16:02 PM by Manager on {today}: Left at 3:16, not 5:12",
            $"Out changed from 9/8/2026 3:16:02 PM to 9/8/2026 3:20:00 PM by Manager on {today}: Checked the camera: 3:20",
        ], row.Notes);
    }

    [Fact]
    public async Task Kiosk_punches_carry_no_marks()
    {
        var ada = await Db.AddEmployeeAsync();
        await Db.Punches.PunchAsync(ada, "1234", PunchDirection.In);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone).DateTime);

        var row = (await Db.Reports.ShiftDetailReportAsync(_manager, today.AddDays(-1), today.AddDays(1))).Employees.Single().Rows.Single();

        Assert.Equal((ShiftDetailKind.NotCounted, false, false), (row.Kind, row.InChanged, row.OutChanged));
        Assert.Equal(["Not counted. No punch-out: add it on the Corrections tab."], row.Notes);
    }

    [Fact]
    public async Task Voided_moved_and_unmatched_punches_are_listed_not_paid()
    {
        var ada = await Db.AddEmployeeAsync();
        await Shift(ada, new(2026, 9, 9, 8, 0, 0), new(2026, 9, 9, 16, 0, 0));
        var stray = await Add(ada, PunchDirection.In, new DateTime(2026, 9, 10, 17, 9, 18));
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.VoidAsync(_manager, stray, "Punched in by mistake on her day off"));
        var late = await Add(ada, PunchDirection.In, new DateTime(2026, 9, 19, 23, 58, 0));
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.AdjustAsync(
            _manager, late, PunchDirection.In, new DateTime(2026, 9, 20, 0, 2, 0), "Arrived after midnight"));
        await Add(ada, PunchDirection.Out, new DateTime(2026, 9, 12, 14, 0, 0));        // no IN before it

        var report = await Db.Reports.ShiftDetailReportAsync(_manager, From, To);
        var rows = report.Employees.Single().Rows;

        Assert.Equal(
            [ShiftDetailKind.Counted, ShiftDetailKind.Voided, ShiftDetailKind.MissingIn, ShiftDetailKind.Moved],
            rows.Select(r => r.Kind));
        Assert.Equal((1, 480L), (report.Employees[0].Shifts, report.Employees[0].Minutes));
        Assert.Equal(new DateTime(2026, 9, 10, 17, 9, 18), rows[1].InLocal);
        Assert.EndsWith("Punched in by mistake on her day off", rows[1].Notes[^1], StringComparison.Ordinal);
        Assert.StartsWith("In at 9/10/2026 5:09:18 PM voided by Manager", rows[1].Notes[^1], StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 9, 12, 14, 0, 0), rows[2].OutLocal);
        Assert.StartsWith("No punch-in before this punch-out", rows[2].Notes[^1], StringComparison.Ordinal);
        Assert.StartsWith("In changed from 9/19/2026 11:58:00 PM to 9/20/2026 12:02:00 AM", rows[3].Notes[^1], StringComparison.Ordinal);
        Assert.All(rows.Skip(1), r => Assert.Null(r.Hours));

        // The moved punch shows up again, at its new time, in the next period.
        var next = await Db.Reports.ShiftDetailReportAsync(_manager, new(2026, 9, 20), new(2026, 10, 3));
        var moved = next.Employees.Single().Rows.Single();
        Assert.Equal((new DateTime(2026, 9, 20, 0, 2, 0), true), (moved.InLocal!.Value, moved.InChanged));
        Assert.StartsWith("In changed from 9/19/2026 11:58:00 PM", moved.Notes[^2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_punch_changed_several_times_gets_one_line_showing_where_it_ended()
    {
        var ada = await Db.AddEmployeeAsync();
        // Moved out of the period and back in: shown once, at its final time, with both changes.
        var back = await Add(ada, PunchDirection.In, new DateTime(2026, 9, 10, 8, 0, 0));
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.AdjustAsync(
            _manager, back, PunchDirection.In, new DateTime(2026, 9, 25, 8, 0, 0), "Wrong week picked by mistake"));
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.AdjustAsync(
            _manager, await NewestPunch(), PunchDirection.In, new DateTime(2026, 9, 11, 8, 0, 0), "Should have been the 11th"));
        await Add(ada, PunchDirection.Out, new DateTime(2026, 9, 11, 12, 0, 0));
        // Moved out of the period, then voided there: one voided line with the whole story.
        var gone = await Add(ada, PunchDirection.In, new DateTime(2026, 9, 14, 8, 0, 0));
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.AdjustAsync(
            _manager, gone, PunchDirection.In, new DateTime(2026, 9, 28, 8, 0, 0), "Belongs to the next period"));
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.VoidAsync(_manager, await NewestPunch(), "Was never worked at all"));

        var rows = (await Db.Reports.ShiftDetailReportAsync(_manager, From, To)).Employees.Single().Rows;

        Assert.Equal([ShiftDetailKind.Counted, ShiftDetailKind.Voided], rows.Select(r => r.Kind));
        Assert.Equal(new DateTime(2026, 9, 11, 8, 0, 0), rows[0].InLocal);
        Assert.Equal(240, rows[0].Minutes);
        Assert.Equal(4, rows[0].Notes.Count);   // In added, moved out, moved back; Out added
        Assert.StartsWith("In changed from 9/25/2026 8:00:00 AM to 9/11/2026 8:00:00 AM", rows[0].Notes[2], StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 9, 14, 8, 0, 0), rows[1].InLocal);
        Assert.Equal(3, rows[1].Notes.Count);
        Assert.StartsWith("In changed from 9/14/2026 8:00:00 AM to 9/28/2026 8:00:00 AM", rows[1].Notes[1], StringComparison.Ordinal);
        Assert.StartsWith("In at 9/28/2026 8:00:00 AM voided", rows[1].Notes[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_punch_that_leaves_the_period_twice_is_listed_once()
    {
        var ada = await Db.AddEmployeeAsync();
        var punch = await Add(ada, PunchDirection.In, new DateTime(2026, 9, 10, 8, 0, 0));
        (DateTime At, string Why)[] moves =
        [
            (new(2026, 9, 25, 8, 0, 0), "Moved to the wrong week"),
            (new(2026, 9, 11, 8, 0, 0), "Back to the right week"),
            (new(2026, 9, 26, 8, 0, 0), "Belongs to the next period"),
        ];
        foreach (var (at, why) in moves)
        {
            Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.AdjustAsync(_manager, punch, PunchDirection.In, at, why));
            punch = await NewestPunch();
        }

        var row = (await Db.Reports.ShiftDetailReportAsync(_manager, From, To)).Employees.Single().Rows.Single();

        Assert.Equal((ShiftDetailKind.Moved, new DateTime(2026, 9, 11, 8, 0, 0)), (row.Kind, row.InLocal!.Value));
        Assert.Equal(4, row.Notes.Count);
        Assert.EndsWith("Belongs to the next period", row.Notes[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pdf_prints_the_old_layout_and_is_audited()
    {
        var ada = await Db.AddEmployeeAsync(first: "Zoë", last: "O'Brien (Jo)");
        await Shift(ada, new(2026, 9, 8, 6, 51, 58), new(2026, 9, 8, 13, 33, 29));
        var head = await Db.ScalarAsync<long>("SELECT max(seq) FROM audit_log;");
        var hash = await Db.ScalarAsync<string>("SELECT row_hash FROM audit_log WHERE seq = $s;", ("$s", head));
        var path = Path.Combine(Db.Folder, "hours.pdf");

        var result = await Db.Reports.ExportShiftDetailPdfAsync(_manager, From, To, path);

        var bytes = File.ReadAllBytes(path);
        Assert.StartsWith("%PDF-1.4", Encoding.ASCII.GetString(bytes, 0, 8), StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(1, result.Rows);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Sha256);
        Assert.Equal(result.Sha256, await Db.ScalarAsync<string>("""
            SELECT json_extract(after_json, '$.sha256') FROM audit_log
             WHERE action = 'REPORT_EXPORT' AND actor_id = $by AND json_extract(after_json, '$.kind') = 'shift_detail';
            """, ("$by", _manager.Id)));

        var text = PdfText(bytes);
        Assert.Contains("Start Date: 9/6/2026     End Date: 9/19/2026", text);
        Assert.Contains("Zoë O'Brien (Jo)", text);
        Assert.Contains("Sum of Total Hours   6.70", text);
        Assert.Contains("9/8/2026 6:51:58 AM *", text);
        Assert.Contains("9/8/2026 1:33:29 PM *", text);
        Assert.Contains("Tue", text);
        Assert.Contains("Page 1 of 1", text);
        Assert.Contains($"Audit log entry {head}, {hash[..16]}", text);
        Assert.Contains("Correct it in PunchClock (Admin, Corrections tab)", text);
        Assert.Empty(await Db.VerifyAsync());
    }

    [Fact]
    public async Task The_pdf_is_only_written_as_a_pdf()
    {
        var other = Path.Combine(Db.Folder, "hours.csv");
        await File.WriteAllTextAsync(other, "keep");

        await Assert.ThrowsAsync<ArgumentException>(() => Db.Reports.ExportShiftDetailPdfAsync(_manager, From, To, other));
        await Assert.ThrowsAsync<ArgumentException>(() => Db.Reports.ExportPayReportAsync(_manager, From, To, Path.ChangeExtension(other, ".pdf")));

        Assert.Equal("keep", await File.ReadAllTextAsync(other));
        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT count(*) FROM audit_log WHERE action = 'REPORT_EXPORT';"));
    }

    [Fact]
    public async Task Only_admins_and_managers_print_it() =>
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Db.Reports.ShiftDetailReportAsync(_manager with { IsActive = false }, From, To));

    [Fact]
    public async Task Long_periods_break_across_pages_and_repeat_the_name()
    {
        var ada = await Db.AddEmployeeAsync();
        for (var day = 1; day <= 30; day++)
        {
            await Shift(ada, new(2026, 9, day, 8, 0, 0), new(2026, 9, day, 16, 0, 0));
        }

        var report = await Db.Reports.ShiftDetailReportAsync(_manager, new(2026, 9, 1), new(2026, 9, 30));
        var many = report with { Employees = [.. Enumerable.Repeat(report.Employees[0], 3)] };

        var text = PdfText(ShiftDetailPdf.Render(many));

        Assert.Contains("Ada Lovelace (continued)", text);
        Assert.Matches(@"Page 1 of [3-9]", text);
        Assert.Contains("All employees", text);
        Assert.Contains("720.00", text);   // 3 x 30 x 8 hours
    }

    [Fact]
    public void An_empty_period_still_prints()
    {
        var report = new ShiftDetailReport(From, To, [], new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.FromHours(-4)), "Ann (ann)", 0, "");

        var text = PdfText(ShiftDetailPdf.Render(report));

        Assert.Contains("No punches in this period.", text);
        var empty = Baseline(report, "No punches in this period.");
        var summary = Baseline(report, "Summary");
        Assert.True(summary - empty >= 12, $"Summary at {summary} overlaps the message at {empty}.");
        Assert.Contains("Printed September 23, 2026 9:00 AM by Ann (ann)", text);
        Assert.DoesNotContain("Audit log entry", text);
    }

    [Fact]
    public void Notes_longer_than_a_page_carry_over_and_are_all_printed()
    {
        var notes = Enumerable.Range(1, 130).Select(i => $"Note number {i} about this punch").ToList();
        var row = new ShiftDetailRow(ShiftDetailKind.Counted, new(2026, 9, 8, 8, 0, 0), new(2026, 9, 8, 16, 0, 0), 480, true, true, notes);
        var report = new ShiftDetailReport(From, To, [new ShiftDetailEmployee(1, null, "Ada", "Lovelace", [row], 1, 480)],
            new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.FromHours(-4)), "Ann (ann)", 7, new string('a', 64));

        var text = PdfText(ShiftDetailPdf.Render(report));

        Assert.All(notes, n => Assert.Contains(n + "\n", text + "\n"));
        Assert.Contains("Notes continued from the previous page", text);
        Assert.Contains("Ada Lovelace (continued)", text);
        Assert.Single(Regex.Matches(text, "9/8/2026 8:00:00 AM"));   // the shift itself is printed once
    }

    [Theory]
    [InlineData("Zoë", "Zo\\353")]
    [InlineData("a(b)c\\", "a\\(b\\)c\\\\")]
    [InlineData("€ – “x”", "\\200 \\226 \\223x\\224")]
    [InlineData("Łukasz 李", "?ukasz ?")]
    [InlineData("tab\there", "tab here")]
    public void Pdf_text_is_winansi_and_escaped(string text, string expected)
    {
        var pdf = new PdfDocument();
        pdf.AddPage();
        pdf.Text(0, 0, text, PdfFont.Regular, 10);

        var content = Inflate(pdf.Build("t", "a", DateTimeOffset.UnixEpoch)).Single();

        Assert.Contains($"({expected}) Tj", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrapping_breaks_at_spaces_and_cuts_words_too_long_for_a_line()
    {
        var lines = PdfDocument.Wrap("one two three " + new string('x', 80), PdfFont.Regular, 10, 100);

        Assert.Equal("one two three", lines[0]);
        Assert.All(lines, l => Assert.True(PdfDocument.Width(l, PdfFont.Regular, 10) <= 100));
        Assert.Equal(80, string.Concat(lines.Skip(1)).Length);
    }

    /// <summary>Distance from the page top of the first <c>Tj</c> drawing <paramref name="text"/>.</summary>
    private static double Baseline(ShiftDetailReport report, string text)
    {
        var content = Inflate(ShiftDetailPdf.Render(report)).First();
        var m = Regex.Match(content, @"([\d.]+) ([\d.]+) Td \(" + Regex.Escape(text) + @"\) Tj");
        Assert.True(m.Success, $"'{text}' not drawn.");
        return PdfDocument.PageHeight - double.Parse(m.Groups[2].Value, ShiftDetailFormat.Culture);
    }

    private static DateTime Parse(string oldReportTime) => DateTime.Parse(oldReportTime, ShiftDetailFormat.Culture);

    /// <summary>The text drawn on every page, one string per <c>Tj</c>, decoded from WinAnsi.</summary>
    private static string PdfText(byte[] pdf) =>
        string.Join('\n', Inflate(pdf).SelectMany(c => TextOperator().Matches(c).Select(m => Unescape(m.Groups[1].Value))));

    private static IEnumerable<string> Inflate(byte[] pdf)
    {
        var raw = Encoding.Latin1.GetString(pdf);
        foreach (Match m in StreamBody().Matches(raw))
        {
            using var zlib = new ZLibStream(new MemoryStream(Encoding.Latin1.GetBytes(m.Groups[1].Value)), CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.Latin1);
            yield return reader.ReadToEnd();
        }
    }

    private static string Unescape(string s)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\')
            {
                bytes.Add((byte)s[i]);
            }
            else if (char.IsAsciiDigit(s[i + 1]))
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                bytes.Add((byte)s[++i]);
            }
        }

        return CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString([.. bytes]);
    }

    [GeneratedRegex(@"stream\n(.*?)\nendstream", RegexOptions.Singleline)]
    private static partial Regex StreamBody();

    [GeneratedRegex(@"\(((?:\\.|[^\\)])*)\) Tj")]
    private static partial Regex TextOperator();
}
