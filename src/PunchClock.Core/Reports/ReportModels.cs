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
    public decimal Hours => Math.Round(Minutes / 60m, 2, MidpointRounding.AwayFromZero);

    public decimal ElapsedHours => Math.Round(ElapsedMinutes / 60m, 2, MidpointRounding.AwayFromZero);
}

/// <summary>A shift that started in the period but the report does not pay, and why.</summary>
public sealed record UncountedShift(long EmployeeId, string Name, DateTime InLocal, DateTime? OutLocal, string Reason);

public sealed record PayReport(DateOnly From, DateOnly To, IReadOnlyList<PayReportLine> Lines, IReadOnlyList<UncountedShift> NotCounted)
{
    public long TotalMinutes => Lines.Sum(l => l.Minutes);
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

    private static long Minute(DateTime t) =>
        t.AddMilliseconds(500).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond / TimeSpan.TicksPerMinute;
}
