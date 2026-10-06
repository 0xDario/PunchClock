using System.Globalization;
using System.Text;
using PunchClock.Migration.Analysis;

namespace PunchClock.Migration.Reporting;

/// <summary>
/// Writes the human-readable report and the CSVs a manager uses to reconcile
/// hours against the old app. Plain text, so it opens in Notepad on any PC.
/// </summary>
public static class ImportReport
{
    public const string ReportFile = "import-report.txt";
    public const string FindingsFile = "findings.csv";
    public const string EmployeeHoursFile = "hours-by-employee.csv";
    public const string MonthHoursFile = "hours-by-month.csv";

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly UTF8Encoding Utf8Bom = new(true);

    /// <param name="withTemporaryPins">List the temporary PINs. Only for the copy that goes with the export, never the one left on the PC.</param>
    public static void Write(string directory, ImportPlan plan, string timeZoneSource, ImportOutcome? outcome, bool withTemporaryPins = false)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ReportFile), Text(plan, timeZoneSource, outcome, withTemporaryPins), Utf8Bom);
        WriteCsv(Path.Combine(directory, FindingsFile),
            ["level", "code", "table", "legacy_id", "legacy_employee_id", "description", "detail"],
            plan.Findings.Select(f => new[]
            {
                f.Level.ToString(), f.Code.ToString(), f.Table, f.LegacyId?.ToString(Inv), f.LegacyEmployeeId?.ToString(Inv),
                FindingInfo.Describe(f.Code), f.Message,
            }));
        WriteCsv(Path.Combine(directory, EmployeeHoursFile),
            ["legacy_employee_id", "name", "shift_rows", "closed_shifts", "open_shifts", "hours", "hh_mm", "elapsed_hours"],
            plan.Totals.ByEmployee.Select(e => new[]
            {
                e.LegacyEmployeeId.ToString(Inv), e.Name, e.ShiftRows.ToString(Inv), e.ClosedShifts.ToString(Inv),
                e.OpenShifts.ToString(Inv), Hours(e.WallClock), HhMm(e.WallClock), Hours(e.Elapsed),
            }));
        WriteCsv(Path.Combine(directory, MonthHoursFile),
            ["month", "legacy_employee_id", "name", "legacy_report_shifts", "legacy_report_hours", "legacy_report_hh_mm", "all_shifts_started", "all_hours_started"],
            plan.Totals.ByMonth.OrderBy(m => m.Month).ThenBy(m => m.LegacyEmployeeId).Select(m => new[]
            {
                m.Month, m.LegacyEmployeeId.ToString(Inv), m.Name, m.LegacyReportShifts.ToString(Inv), Hours(m.LegacyReport),
                HhMm(m.LegacyReport), m.ByStartMonthShifts.ToString(Inv), Hours(m.ByStartMonth),
            }));
    }

    public static string Text(ImportPlan plan, string timeZoneSource, ImportOutcome? outcome, bool withTemporaryPins = false)
    {
        var m = plan.Export.Manifest;
        var t = plan.Totals;
        var review = plan.Findings.Where(f => f.Level == FindingLevel.Review).ToList();
        var info = plan.Findings.Where(f => f.Level == FindingLevel.Info).ToList();
        var sb = new StringBuilder();

        sb.AppendLine(outcome is null ? "PunchClock legacy import: CHECK ONLY, nothing was written" : "PunchClock legacy import: IMPORTED");
        sb.AppendLine(new string('=', 64));
        if (outcome is not null)
        {
            Line(sb, "Database", outcome.DatabasePath);
            Line(sb, "Imported at (UTC)", outcome.ImportedAtUtc.ToString("yyyy-MM-dd HH:mm:ss'Z'", Inv));
            Line(sb, "Import batch", outcome.ImportRunId.ToString(Inv));
            Line(sb, "Audit log head", $"seq {outcome.ChainSeq}, hash {outcome.ChainHash}");
            sb.AppendLine("  Print this page and keep it with the PunchClock.accdb backup. The audit log head proves");
            sb.AppendLine("  later that nothing imported today was altered.");
        }
        if (outcome is { TemporaryPins.Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"Temporary PINs ({outcome.TemporaryPins.Count}): these employees had no PIN in the old app.");
            if (withTemporaryPins)
            {
                foreach (var (id, pin) in outcome.TemporaryPins)
                    sb.AppendLine($"  Employee {id.ToString(Inv),-8}{Clip(Name(plan, id), 27),-28}PIN {pin}");
                sb.AppendLine("  Give each PIN to its employee only. They must choose a new PIN before their first punch is recorded.");
            }
            else
            {
                sb.AppendLine("  Listed only in the report written next to the export, and on screen at import.");
            }
            sb.AppendLine();
        }
        Line(sb, "Export folder", plan.Export.Folder);
        Line(sb, "Exported by", $"{m.Tool}, {m.ExportedAtUtc}");
        Line(sb, "Source file", m.SourcePath ?? "(not recorded)");
        Line(sb, "Source SHA-256", m.SourceSha256);
        Line(sb, "Manifest SHA-256", m.ManifestSha256);
        Line(sb, "Snapshot", plan.Export.SnapshotVerified ? "present, hash matches" : "not in the export folder");
        Line(sb, "Time zone", $"{plan.TimeZone.Id} ({timeZoneSource})");
        sb.AppendLine();

        sb.AppendLine("Rows");
        Line(sb, "  Employees", $"{t.EmployeeRows} in export, all imported with their legacy IDs");
        Line(sb, "  Shifts", $"{t.ShiftRows} in export, {t.ShiftRows - t.SkippedShiftRows} imported as punches, {t.SkippedShiftRows} skipped (listed below)");
        if (t.OrphanShiftRows > 0)
            Line(sb, "  Orphan shifts", $"{t.OrphanShiftRows} belong to employees that no longer exist; the old report never counted them");
        sb.AppendLine("  Every row, including skipped ones, is stored unchanged in the new database as evidence.");
        if (outcome is not null)
            Line(sb, "  Punches written", outcome.PunchesWritten.ToString(Inv));
        foreach (var w in m.Warnings)
            Line(sb, "  Export warning", w);
        sb.AppendLine();

        sb.AppendLine($"Needs review after import ({review.Count})");
        sb.AppendLine("No legacy value was changed. Fix these on the Corrections tab in the new app, where every correction is logged.");
        AppendFindings(sb, review);
        sb.AppendLine();
        sb.AppendLine($"For information ({info.Count})");
        AppendFindings(sb, info);
        sb.AppendLine();

        sb.AppendLine("Hours per employee, all closed shifts, counted the way the old report counts them");
        sb.AppendLine($"  {"ID",-8}{"Name",-28}{"Shifts",8}{"Open",6}{"Hours",11}");
        foreach (var e in t.ByEmployee)
            sb.AppendLine($"  {e.LegacyEmployeeId.ToString(Inv),-8}{Clip(e.Name, 27),-28}{e.ShiftRows,8}{e.OpenShifts,6}{Hours(e.WallClock),11}");
        sb.AppendLine($"  {"",-8}{"Total",-28}{t.ByEmployee.Sum(e => e.ShiftRows),8}{t.ByEmployee.Sum(e => e.OpenShifts),6}{Hours(t.TotalWallClock),11}");
        sb.AppendLine();
        sb.AppendLine("To reconcile: in the old app, run the hours report for one whole month (start date the 1st,");
        sb.AppendLine("end date the last day). Each employee's total must equal legacy_report_hours for that month");
        sb.AppendLine($"in {MonthHoursFile}. all_hours_started also counts shifts that end after midnight on the last");
        sb.AppendLine("day, which the old report leaves out of every month.");
        return sb.ToString();
    }

    static void AppendFindings(StringBuilder sb, List<Finding> findings)
    {
        if (findings.Count == 0)
        {
            sb.AppendLine("  (none)");
            return;
        }

        foreach (var group in findings.GroupBy(f => f.Code))
        {
            sb.AppendLine($"  {FindingInfo.Describe(group.Key)}: {group.Count()}");
            foreach (var f in group)
            {
                var where = f.Table switch
                {
                    "Shift" => $"Shift {f.LegacyId}, employee {f.LegacyEmployeeId?.ToString(Inv) ?? "none"}",
                    "Employee" when f.LegacyEmployeeId is not null => $"Employee {f.LegacyEmployeeId}",
                    _ => f.Table,
                };
                sb.AppendLine($"    - {where}: {f.Message}");
            }
        }
    }

    static string Name(ImportPlan plan, long legacyId)
    {
        var e = plan.Employees.Single(e => e.LegacyEmployeeId == legacyId);
        return $"{e.FirstName} {e.LastName}".Trim();
    }

    static void Line(StringBuilder sb, string label, string value) => sb.AppendLine($"{label + ":",-20}{value}");

    static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "~";

    public static string Hours(TimeSpan t) => t.TotalHours.ToString("0.00", Inv);

    static string HhMm(TimeSpan t)
    {
        var minutes = (long)Math.Round(t.TotalMinutes, MidpointRounding.AwayFromZero);
        var sign = minutes < 0 ? "-" : "";
        minutes = Math.Abs(minutes);
        return $"{sign}{minutes / 60}:{minutes % 60:00}";
    }

    static void WriteCsv(string path, string[] header, IEnumerable<string?[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', header.Select(Quote))).Append("\r\n");
        foreach (var row in rows)
            sb.Append(string.Join(',', row.Select(Quote))).Append("\r\n");
        // BOM so Excel opens names with accents correctly.
        File.WriteAllText(path, sb.ToString(), Utf8Bom);
    }

    static string Quote(string? s) =>
        s is null ? "" : s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? '"' + s.Replace("\"", "\"\"") + '"' : s;
}

/// <summary>What an import wrote, for the report.</summary>
/// <param name="ChainSeq">Audit log head after the import; with <paramref name="ChainHash"/>, the first external anchor.</param>
/// <param name="TemporaryPins">Legacy employee ID to the PIN issued because the old app had none. Never stored in plain text.</param>
public sealed record ImportOutcome(
    string DatabasePath,
    long ImportRunId,
    DateTime ImportedAtUtc,
    int PunchesWritten,
    long ChainSeq,
    string ChainHash,
    IReadOnlyDictionary<long, string> TemporaryPins);
