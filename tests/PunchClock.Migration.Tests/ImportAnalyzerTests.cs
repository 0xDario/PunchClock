using PunchClock.Migration.Analysis;
using PunchClock.Migration.Export;
using PunchClock.Migration.Reporting;

namespace PunchClock.Migration.Tests;

public sealed class ImportAnalyzerTests : IDisposable
{
    static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    readonly ExportBuilder _export = new();

    public void Dispose() => _export.Dispose();

    ImportPlan Analyze()
    {
        _export.Build();
        return ImportAnalyzer.Analyze(LegacyExport.Load(_export.Folder), Toronto);
    }

    static IEnumerable<FindingCode> Codes(ImportPlan plan, long shiftId) =>
        plan.Shifts.Single(s => s.Source.ShiftId == shiftId).Flags;

    [Fact]
    public void Flags_the_dummy_shift_created_with_each_employee_and_keeps_it()
    {
        _export.Employee(1, "Ann", "Lee")
            .Shift(10, 1, "2025-01-06 08:00:00", "2025-01-06 08:00:00")
            .Shift(11, 1, "2025-01-07 08:00:00", "2025-01-07 08:00:00");

        var plan = Analyze();

        Assert.Equal(2, plan.Shifts.Count);
        Assert.Equal([FindingCode.DummyShift], Codes(plan, 10));
        Assert.Equal([FindingCode.ZeroLengthShift], Codes(plan, 11));
        Assert.Equal(FindingLevel.Info, plan.Findings.Single(f => f.Code == FindingCode.DummyShift).Level);
    }

    [Fact]
    public void Keeps_orphan_shifts_under_one_inactive_placeholder_per_missing_employee()
    {
        _export.Employee(1, "Ann", "Lee")
            .Shift(10, 1, "2025-01-06 08:00:00", "2025-01-06 16:00:00")
            .Shift(11, 7, "2025-01-06 08:00:00", "2025-01-06 12:00:00")
            .Shift(12, 7, "2025-01-07 08:00:00", "2025-01-07 12:00:00");

        var plan = Analyze();

        var placeholder = Assert.Single(plan.Employees, e => e.IsPlaceholder);
        Assert.Equal(7, placeholder.LegacyEmployeeId);
        Assert.False(placeholder.IsActive);
        Assert.Null(placeholder.LegacyPin);
        Assert.All(plan.Shifts.Where(s => s.Source.EmployeeId == 7), s =>
        {
            Assert.Same(placeholder, s.Employee);
            Assert.Contains(FindingCode.OrphanShift, s.Flags);
        });
        Assert.Equal(8, plan.Totals.ByEmployee.Single(e => e.LegacyEmployeeId == 7).WallClock.TotalHours);
    }

    [Fact]
    public void Tells_a_live_punch_in_apart_from_a_missed_punch_out()
    {
        _export.Employee(1, "Ann", "Lee")
            .Shift(10, 1, "2025-01-06 08:00:00", null)
            .Shift(11, 1, "2025-01-07 08:00:00", "2025-01-07 16:00:00")
            .Shift(12, 1, "2025-01-08 08:00:00", null);

        var plan = Analyze();

        Assert.Contains(FindingCode.OpenShiftStale, Codes(plan, 10));
        Assert.Contains(FindingCode.OpenShiftCurrent, Codes(plan, 12));
        Assert.Equal(2, plan.Totals.ByEmployee.Single().OpenShifts);
    }

    [Fact]
    public void Flags_negative_long_overlapping_and_out_of_order_shifts()
    {
        _export.Employee(1, "Ann", "Lee")
            .Shift(10, 1, "2025-01-06 08:00:00", "2025-01-06 07:00:00")
            .Shift(11, 1, "2025-01-07 08:00:00", "2025-01-08 02:00:00")
            .Shift(12, 1, "2025-01-07 20:00:00", "2025-01-07 22:00:00")
            .Shift(13, 1, "2025-01-05 08:00:00", "2025-01-05 09:00:00");

        var plan = Analyze();

        Assert.Contains(FindingCode.NegativeShift, Codes(plan, 10));
        Assert.Contains(FindingCode.LongShift, Codes(plan, 11));
        Assert.Contains(FindingCode.OverlappingShift, Codes(plan, 12));
        Assert.Contains(FindingCode.OutOfOrderShiftId, Codes(plan, 13));
    }

    [Fact]
    public void Resolves_local_times_to_utc_and_flags_dst_edges()
    {
        _export.Employee(1, "Ann", "Lee")
            .Shift(10, 1, "2025-03-08 22:00:00", "2025-03-09 06:00:00")
            .Shift(11, 1, "2025-07-01 09:00:00", "2025-07-01 17:00:00")
            // 01:30 on 2025-11-02 happens twice in Toronto; 02:30 on 2026-03-08 never happens.
            .Shift(12, 1, "2025-11-02 01:30:00", "2025-11-02 03:00:00")
            .Shift(13, 1, "2026-03-08 02:30:00", "2026-03-08 04:00:00");

        var plan = Analyze();

        Assert.Contains(FindingCode.CrossesDstChange, Codes(plan, 10));
        Assert.Contains(FindingCode.DstAmbiguousTime, Codes(plan, 12));
        Assert.Contains(FindingCode.DstNonexistentTime, Codes(plan, 13));

        var summer = plan.Shifts.Single(s => s.Source.ShiftId == 11);
        Assert.Equal(new DateTime(2025, 7, 1, 13, 0, 0, DateTimeKind.Utc), summer.In!.Value.Utc);
        Assert.Equal(-240, summer.In.Value.UtcOffsetMinutes);
        Assert.Empty(summer.Flags);

        var spring = plan.Shifts.Single(s => s.Source.ShiftId == 10);
        Assert.Equal(TimeSpan.FromHours(8), spring.WallClockDuration);
        Assert.Equal(TimeSpan.FromHours(7), spring.ElapsedDuration);
    }

    [Fact]
    public void Flags_pins_and_names_without_changing_them()
    {
        _export.Employee(1, "Ann", "Lee", pin: "42")
            .Employee(2, "ann", "lee", pin: null)
            .Employee(3, " ", "Kim");

        var plan = Analyze();

        Assert.Equal("42", plan.Employees[0].LegacyPin);
        Assert.Contains(plan.Findings, f => f.Code == FindingCode.PinLostLeadingZeros && f.LegacyId == 1);
        Assert.Contains(plan.Findings, f => f.Code == FindingCode.PinMissing && f.LegacyId == 2);
        Assert.Equal(2, plan.Findings.Count(f => f.Code == FindingCode.DuplicateName));
        Assert.Contains(plan.Findings, f => f.Code == FindingCode.MissingName && f.LegacyId == 3);
        Assert.Equal(3, plan.Findings.Count(f => f.Code == FindingCode.NoShifts));
    }

    [Fact]
    public void Imports_everyone_as_active_when_the_export_predates_IsActive()
    {
        _export.WithIsActive = false;
        _export.Employee(1, "Ann", "Lee");

        var plan = Analyze();

        Assert.True(plan.Employees.Single().IsActive);
        Assert.Contains(plan.Findings, f => f.Code == FindingCode.IsActiveColumnMissing);
    }

    [Fact]
    public void Month_totals_reproduce_the_old_report_dropping_shifts_that_end_after_the_last_day()
    {
        _export.Employee(1, "Ann", "Lee")
            .Shift(10, 1, "2025-01-15 08:00:00", "2025-01-15 16:00:00")
            .Shift(11, 1, "2025-01-31 22:00:00", "2025-02-01 02:00:00")
            .Shift(12, 1, "2025-02-03 08:00:00", "2025-02-03 12:00:00")
            .Shift(13, 1, "2025-02-04 08:00:00", null);

        var plan = Analyze();

        var jan = plan.Totals.ByMonth.Single(m => m.Month == "2025-01");
        Assert.Equal(8, jan.LegacyReport.TotalHours);
        Assert.Equal(12, jan.ByStartMonth.TotalHours);
        var feb = plan.Totals.ByMonth.Single(m => m.Month == "2025-02");
        Assert.Equal(4, feb.LegacyReport.TotalHours);
        Assert.Equal(1, feb.ByStartMonthShifts);
        Assert.Equal(16, plan.Totals.ByEmployee.Single().WallClock.TotalHours);
    }

    [Fact]
    public void Writes_a_plain_text_report_and_reconciliation_csvs()
    {
        _export.Employee(1, "Ann", "Lee")
            .Shift(10, 1, "2025-01-06 08:00:00", "2025-01-06 08:00:00")
            .Shift(11, 9, "2025-01-06 09:00:00", "2025-01-06 17:30:00");
        var plan = Analyze();
        var dir = Path.Combine(_export.Folder, "report");

        ImportReport.Write(dir, plan, "export manifest", outcome: null);

        var text = File.ReadAllText(Path.Combine(dir, ImportReport.ReportFile));
        Assert.Contains("CHECK ONLY", text);
        Assert.Contains("Shift 11, employee 9", text);
        var hours = File.ReadAllLines(Path.Combine(dir, ImportReport.EmployeeHoursFile));
        Assert.Contains(hours, l => l.StartsWith("9,Unknown (legacy #9),1,1,0,8.50,8:30,"));
        Assert.True(File.Exists(Path.Combine(dir, ImportReport.FindingsFile)));
        Assert.True(File.Exists(Path.Combine(dir, ImportReport.MonthHoursFile)));
    }
}
