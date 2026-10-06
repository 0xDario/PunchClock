using System.Globalization;

namespace PunchClock.Migration.Analysis;

/// <param name="ShiftRows">Legacy rows for this employee, including skipped ones.</param>
/// <param name="ClosedShifts">Imported shifts with both punches; the old report counts exactly these (dummy shifts add 0 hours).</param>
/// <param name="WallClock">Their hours as the old report counts them (DateDiff minutes).</param>
public sealed record EmployeeTotals(
    long LegacyEmployeeId,
    string Name,
    int ShiftRows,
    int ClosedShifts,
    int OpenShifts,
    TimeSpan WallClock,
    TimeSpan Elapsed);

/// <summary>
/// Hours for one employee and calendar month, computed two ways.
/// <see cref="LegacyReport"/> reproduces the old Access report run for that
/// month (StartDate = the 1st, EndDate = the last day): closed shifts with
/// <c>TimeIn &gt;= StartDate AND TimeOut &lt; EndDate + 1</c>, summing
/// <c>DateDiff("n", TimeIn, TimeOut)</c> minutes. <see cref="ByStartMonth"/> counts every closed shift in the month it
/// started; the difference is shifts the old report silently drops because
/// they end after midnight on the last day.
/// </summary>
public sealed record MonthTotals(
    long LegacyEmployeeId,
    string Name,
    string Month,
    int LegacyReportShifts,
    TimeSpan LegacyReport,
    int ByStartMonthShifts,
    TimeSpan ByStartMonth);

/// <param name="OrphanShiftRows">Shifts whose employee does not exist. The old report joins Employee, so it never counted them either.</param>
public sealed record ReconciliationTotals(
    int EmployeeRows,
    int ShiftRows,
    int SkippedShiftRows,
    int OrphanShiftRows,
    IReadOnlyList<EmployeeTotals> ByEmployee,
    IReadOnlyList<MonthTotals> ByMonth)
{
    public TimeSpan TotalWallClock => ByEmployee.Aggregate(TimeSpan.Zero, (a, e) => a + e.WallClock);

    public static ReconciliationTotals Compute(
        IReadOnlyList<PlannedEmployee> employees,
        IReadOnlyList<PlannedShift> shifts)
    {
        var byEmployee = employees
            .Select(e =>
            {
                var mine = shifts.Where(s => ReferenceEquals(s.Employee, e)).ToList();
                return new EmployeeTotals(
                    e.LegacyEmployeeId,
                    DisplayName(e),
                    mine.Count,
                    mine.Count(s => s.WallClockDuration is not null),
                    mine.Count(s => s.In is not null && s.Out is null),
                    Sum(mine.Select(s => s.WallClockDuration is null ? null : (TimeSpan?)AccessDateDiffMinutes(s.In!.Value.Local, s.Out!.Value.Local))),
                    Sum(mine.Select(s => s.ElapsedDuration)));
            })
            .ToList();

        // Month totals follow the old report on the raw rows: every closed shift of an existing
        // employee counts, including the zero-length rows the importer skips.
        var byMonth = new List<MonthTotals>();
        foreach (var e in employees)
        {
            var closed = shifts
                .Where(s => ReferenceEquals(s.Employee, e) && s.Source.TimeIn is not null && s.Source.TimeOut is not null)
                .Select(s => (In: s.Source.TimeIn!.Value, Out: s.Source.TimeOut!.Value))
                .ToList();
            foreach (var month in closed.Select(s => MonthStart(s.In)).Distinct().Order())
            {
                var next = month.AddMonths(1);
                var started = closed.Where(s => MonthStart(s.In) == month).ToList();
                var legacy = started.Where(s => s.Out < next).ToList();
                byMonth.Add(new MonthTotals(
                    e.LegacyEmployeeId,
                    DisplayName(e),
                    month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    legacy.Count,
                    Sum(legacy.Select(s => (TimeSpan?)AccessDateDiffMinutes(s.In, s.Out))),
                    started.Count,
                    Sum(started.Select(s => (TimeSpan?)(s.Out - s.In)))));
            }
        }

        return new ReconciliationTotals(
            employees.Count,
            shifts.Count,
            shifts.Count(s => s.Disposition == Disposition.Skipped),
            shifts.Count(s => s.Employee is null),
            byEmployee,
            byMonth);
    }

    public static string DisplayName(PlannedEmployee e) => $"{e.FirstName} {e.LastName}".Trim();

    /// <summary>
    /// <c>DateDiff("n", timeIn, timeOut)</c> as the old pay report sums it: both ends rounded
    /// to the second, then the minute boundaries crossed are counted, so 08:00:30 to 08:10:20
    /// is 10 minutes, not 9.83. Same rule as the exporter's validator (PR #4).
    /// </summary>
    public static TimeSpan AccessDateDiffMinutes(DateTime timeIn, DateTime timeOut)
    {
        static long Minute(DateTime t) =>
            (t.AddMilliseconds(500).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond) / TimeSpan.TicksPerMinute;
        return TimeSpan.FromMinutes(Minute(timeOut) - Minute(timeIn));
    }

    static DateTime MonthStart(DateTime local) => new(local.Year, local.Month, 1);

    static TimeSpan Sum(IEnumerable<TimeSpan?> values) =>
        values.Aggregate(TimeSpan.Zero, (acc, v) => acc + (v ?? TimeSpan.Zero));
}
