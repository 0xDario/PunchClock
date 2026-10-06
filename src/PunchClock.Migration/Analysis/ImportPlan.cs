using PunchClock.Migration.Export;

namespace PunchClock.Migration.Analysis;

/// <summary>A legacy local time resolved to an instant using the site's time zone.</summary>
public readonly record struct ResolvedTime(DateTime Local, DateTime Utc, int UtcOffsetMinutes);

/// <summary>What the importer does with a legacy row. Every row is also kept verbatim as evidence.</summary>
public enum Disposition
{
    Imported,
    ImportedFlagged,

    /// <summary>No punches are created; the raw row and the reason are still stored.</summary>
    Skipped,
}

public sealed record PlannedEmployee(
    long LegacyEmployeeId,
    string FirstName,
    string LastName,
    string? LegacyPin,
    bool IsActive,
    LegacyEmployee Source);

/// <param name="Employee">Null for an orphan shift whose employee does not exist.</param>
public sealed record PlannedShift(
    LegacyShift Source,
    PlannedEmployee? Employee,
    ResolvedTime? In,
    ResolvedTime? Out,
    IReadOnlyList<FindingCode> Flags,
    Disposition Disposition)
{
    /// <summary>
    /// TimeOut minus TimeIn on the wall clock, which is what the old Access report
    /// summed. Null unless both times exist. Can be zero or negative.
    /// </summary>
    public TimeSpan? WallClockDuration =>
        In is { } i && Out is { } o ? o.Local - i.Local : null;

    /// <summary>Elapsed real time. Differs from <see cref="WallClockDuration"/> only across a DST change.</summary>
    public TimeSpan? ElapsedDuration =>
        In is { } i && Out is { } o ? o.Utc - i.Utc : null;
}

public sealed class ImportPlan
{
    public required LegacyExport Export { get; init; }
    public required TimeZoneInfo TimeZone { get; init; }
    public required IReadOnlyList<PlannedEmployee> Employees { get; init; }
    public required IReadOnlyList<PlannedShift> Shifts { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required ReconciliationTotals Totals { get; init; }
}
