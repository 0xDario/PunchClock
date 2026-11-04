using PunchClock.Core.Punches;

namespace PunchClock.Core.Reports;

/// <summary>An effective IN and the effective punch after it, as the schema's <c>shift_v</c> pairs them.</summary>
/// <param name="Out">The closing OUT; null when the next punch is another IN or there is none yet.</param>
public sealed record ShiftRecord(long EmployeeId, Punch In, Punch? Out);

/// <summary>Every punch in a period, superseded ones included, for the punches export.</summary>
/// <param name="SupersededByCorrectionId">The correction that replaced or voided this punch; null while it counts.</param>
/// <param name="CreatedByCorrectionId">The correction that created this punch, for <see cref="PunchSource.Correction"/>.</param>
public sealed record PunchExportRow(
    Punch Punch,
    string EmployeeName,
    long? LegacyId,
    long? SupersededByCorrectionId,
    long? CreatedByCorrectionId);

/// <summary>One correction with the punch it changed and the account that made it, for the corrections export.</summary>
public sealed record CorrectionExportRow(
    long Id,
    DateTimeOffset CreatedUtc,
    CorrectionAction Action,
    long EmployeeId,
    string EmployeeName,
    long? LegacyId,
    Punch? Target,
    PunchDirection? NewDirection,
    DateTimeOffset? NewOccurredUtc,
    int? NewUtcOffsetMinutes,
    string Reason,
    string ActorUsername,
    string ActorName);

/// <summary>Hours for one employee in a pay period.</summary>
/// <param name="Minutes">The pay figure: the old Access report's rule, see <see cref="PayRules.AccessDateDiffMinutes"/>.</param>
/// <param name="ElapsedMinutes">Real elapsed time of the same shifts; differs from <see cref="Minutes"/> by seconds, and by an hour for a shift across a DST change.</param>
/// <param name="NotCounted">Shifts that started in the period but are not paid in it (see <see cref="UncountedShift"/>).</param>
public sealed record PayReportLine(
    long EmployeeId,
    long? LegacyId,
    string Name,
    int Shifts,
    long Minutes,
    decimal ElapsedMinutes,
    int NotCounted)
{
    public decimal Hours => PayRules.Hours(Minutes);

    public decimal ElapsedHours => Math.Round(ElapsedMinutes / 60m, 2, MidpointRounding.AwayFromZero);
}

/// <summary>A shift that started in the period but the report does not pay, and why.</summary>
public sealed record UncountedShift(long EmployeeId, string Name, DateTime InLocal, DateTime? OutLocal, string Reason);

public sealed record PayReport(DateOnly From, DateOnly To, IReadOnlyList<PayReportLine> Lines, IReadOnlyList<UncountedShift> NotCounted)
{
    public long TotalMinutes => Lines.Sum(l => l.Minutes);
}

/// <summary>One correction and the punch it replaced, for tracing how a punch came to be.</summary>
/// <param name="Target">The punch this correction replaced or voided; null for an added punch.</param>
/// <param name="CreatedPunchId">The punch this correction made; null for a void.</param>
public sealed record CorrectionHistoryRow(
    long Id,
    DateTimeOffset CreatedUtc,
    CorrectionAction Action,
    long EmployeeId,
    Punch? Target,
    long? CreatedPunchId,
    PunchDirection? NewDirection,
    DateTimeOffset? NewOccurredUtc,
    int? NewUtcOffsetMinutes,
    string Reason,
    string ActorName);

public enum ShiftDetailKind
{
    /// <summary>A shift the pay report pays in this period.</summary>
    Counted,

    /// <summary>A shift that starts in the period but the pay report does not pay (no punch-out, or ends after the period).</summary>
    NotCounted,

    /// <summary>A punch a manager voided; it no longer counts.</summary>
    Voided,

    /// <summary>A punch a manager changed to a time outside the period.</summary>
    Moved,

    /// <summary>A punch-out with no punch-in before it.</summary>
    MissingIn,
}

/// <summary>One line of the shift detail report, in site wall-clock time.</summary>
/// <param name="InLocal">The punch-in, or for a voided or moved punch-in, its old time.</param>
/// <param name="OutLocal">The punch-out; null when there is none.</param>
/// <param name="Minutes">Paid minutes (<see cref="PayRules.AccessDateDiffMinutes"/>) for a <see cref="ShiftDetailKind.Counted"/> shift, otherwise null.</param>
/// <param name="InChanged">A manager added or changed the punch-in; <paramref name="Notes"/> says who, when and why.</param>
/// <param name="OutChanged">A manager added or changed the punch-out.</param>
/// <param name="Notes">Corrections and problems, oldest first, ready to print.</param>
public sealed record ShiftDetailRow(
    ShiftDetailKind Kind,
    DateTime? InLocal,
    DateTime? OutLocal,
    long? Minutes,
    bool InChanged,
    bool OutChanged,
    IReadOnlyList<string> Notes)
{
    public decimal? Hours => Minutes is { } m ? PayRules.Hours(m) : null;

    /// <summary>Where the row sorts: its punch-in, or its only punch.</summary>
    public DateTime SortTime => InLocal ?? OutLocal ?? DateTime.MinValue;
}

/// <summary>One employee's shifts in the period.</summary>
/// <param name="Minutes">Same figure as the pay report's <see cref="PayReportLine.Minutes"/>.</param>
public sealed record ShiftDetailEmployee(
    long EmployeeId,
    long? LegacyId,
    string FirstName,
    string LastName,
    IReadOnlyList<ShiftDetailRow> Rows,
    int Shifts,
    long Minutes)
{
    public string Name => $"{FirstName} {LastName}";

    public decimal Hours => PayRules.Hours(Minutes);

    public int NotCounted => Rows.Count(r => r.Kind == ShiftDetailKind.NotCounted);
}

/// <summary>
/// Every shift of every employee in a pay period, with each manager correction and each shift the
/// pay report leaves out, for checking times before pay is worked out. Totals equal the pay report's.
/// </summary>
/// <param name="GeneratedLocal">When the report was made, site wall-clock time.</param>
/// <param name="AuditSeq">The audit log's newest entry when the report was made; printed so the paper copy anchors the log.</param>
public sealed record ShiftDetailReport(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<ShiftDetailEmployee> Employees,
    DateTimeOffset GeneratedLocal,
    string GeneratedBy,
    long AuditSeq,
    string AuditHash)
{
    public long TotalMinutes => Employees.Sum(e => e.Minutes);

    public int TotalShifts => Employees.Sum(e => e.Shifts);
}

/// <summary>The written file and the evidence recorded in the audit log for it.</summary>
public sealed record ExportResult(string Path, int Rows, long Bytes, string Sha256);

public static class PayRules
{
    /// <summary>
    /// <c>DateDiff("n", timeIn, timeOut)</c> as the old pay report sums it: both ends rounded to the
    /// second, then the minute boundaries crossed are counted, so 08:00:30 to 08:10:20 is 10 minutes,
    /// not 9.83. Same rule as the exporter's validator (#4) and the importer's reconciliation (#7).
    /// Applied to site wall-clock times, as the old app stored them.
    /// </summary>
    public static long AccessDateDiffMinutes(DateTime timeIn, DateTime timeOut) => Minute(timeOut) - Minute(timeIn);

    /// <summary>Minutes as hours to two decimals, as the old report printed them (402 minutes is 6.70).</summary>
    public static decimal Hours(long minutes) => Math.Round(minutes / 60m, 2, MidpointRounding.AwayFromZero);

    private static long Minute(DateTime t) =>
        t.AddMilliseconds(500).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond / TimeSpan.TicksPerMinute;
}
