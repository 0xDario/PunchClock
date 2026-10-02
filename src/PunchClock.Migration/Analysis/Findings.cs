namespace PunchClock.Migration.Analysis;

/// <summary>
/// Something in the legacy data a manager should know about. No legacy value
/// is ever changed: a row is imported as recorded, imported and flagged, or
/// skipped with its raw row kept, following the schema design
/// (docs/database/schema-design.md, section 9). Fixes go through the new
/// app's audited correction path afterwards.
/// </summary>
public enum FindingCode
{
    // Shift
    DummyShift,
    ZeroLengthShift,
    OpenShiftCurrent,
    OpenShiftStale,
    OrphanShift,
    MissingEmployeeId,
    MissingTimeIn,
    NegativeShift,
    LongShift,
    OverlappingShift,
    OutOfOrderShiftId,
    DstAmbiguousTime,
    DstNonexistentTime,
    CrossesDstChange,

    // Employee
    PinMissing,
    PinLostLeadingZeros,
    MissingName,
    DuplicateName,
    NoShifts,

    // Export
    IsActiveColumnMissing,
    UnmappedColumn,
    SnapshotMissing,
}

public enum FindingLevel
{
    /// <summary>Expected legacy behaviour, recorded for completeness.</summary>
    Info,

    /// <summary>A manager should look at this after the import.</summary>
    Review,
}

public sealed record Finding(
    FindingCode Code,
    FindingLevel Level,
    string Table,
    long? LegacyId,
    long? LegacyEmployeeId,
    string Message);

public static class FindingInfo
{
    public static FindingLevel LevelOf(FindingCode code) => code switch
    {
        FindingCode.DummyShift or
        FindingCode.OpenShiftCurrent or
        FindingCode.NoShifts or
        FindingCode.IsActiveColumnMissing or
        FindingCode.SnapshotMissing => FindingLevel.Info,
        _ => FindingLevel.Review,
    };

    public static string Describe(FindingCode code) => code switch
    {
        FindingCode.DummyShift => "Skipped: zero-length shift the old app created with each new employee",
        FindingCode.ZeroLengthShift => "Skipped: zero-length shift (punch in and out at the same instant)",
        FindingCode.OpenShiftCurrent => "Employee is punched in right now (latest shift has no punch out)",
        FindingCode.OpenShiftStale => "Missed punch out: shift never closed, later shifts exist",
        FindingCode.OrphanShift => "Skipped: shift belongs to an employee ID that does not exist (the old report never counted it)",
        FindingCode.MissingEmployeeId => "Skipped: shift has no employee ID",
        FindingCode.MissingTimeIn => "Skipped: shift has no punch-in time",
        FindingCode.NegativeShift => "Punch out is before punch in",
        FindingCode.LongShift => "Shift longer than 16 hours",
        FindingCode.OverlappingShift => "Shift starts before the employee's previous shift ended",
        FindingCode.OutOfOrderShiftId => "Shift was added out of time order (likely edited directly in Access)",
        FindingCode.DstAmbiguousTime => "Time falls in the repeated hour when clocks go back",
        FindingCode.DstNonexistentTime => "Time falls in the skipped hour when clocks go forward",
        FindingCode.CrossesDstChange => "Shift spans a daylight-saving change; real hours differ from the old report",
        FindingCode.PinMissing => "Employee has no PIN",
        FindingCode.PinLostLeadingZeros => "PIN is shorter than 3 digits, so Access dropped its leading zeros",
        FindingCode.MissingName => "Employee first or last name is empty",
        FindingCode.DuplicateName => "Another employee has the same name",
        FindingCode.NoShifts => "Employee has no shifts",
        FindingCode.IsActiveColumnMissing => "Export has no IsActive column (data predates Oct 2023); everyone imported as active",
        FindingCode.UnmappedColumn => "Export has a column the importer does not map",
        FindingCode.SnapshotMissing => "The .accdb snapshot is not in the export folder, so its hash could not be re-checked",
        _ => code.ToString(),
    };

    /// <summary>The <c>migration_issue.code</c> a finding is stored under, or null when it is report-only.</summary>
    public static string? SchemaCode(FindingCode code) => code switch
    {
        FindingCode.DummyShift or FindingCode.ZeroLengthShift => "DUMMY_SHIFT",
        FindingCode.OpenShiftCurrent or FindingCode.OpenShiftStale => "OPEN_SHIFT",
        FindingCode.OrphanShift or FindingCode.MissingEmployeeId => "ORPHAN_EMPLOYEE",
        FindingCode.MissingTimeIn => "NULL_TIME_IN",
        FindingCode.NegativeShift => "NEGATIVE_DURATION",
        FindingCode.LongShift => "LONG_SHIFT",
        FindingCode.OverlappingShift => "OVERLAPPING_SHIFT",
        FindingCode.DstAmbiguousTime => "DST_AMBIGUOUS",
        FindingCode.DstNonexistentTime => "DST_INVALID",
        FindingCode.MissingName => "NULL_NAME",
        FindingCode.OutOfOrderShiftId => "OUT_OF_ORDER_ID",
        FindingCode.CrossesDstChange => "CROSSES_DST",
        _ => null,
    };

    /// <summary>Findings that mean no punches are created for the shift.</summary>
    public static bool Skips(FindingCode code) => code is
        FindingCode.DummyShift or FindingCode.ZeroLengthShift or FindingCode.OrphanShift or
        FindingCode.MissingEmployeeId or FindingCode.MissingTimeIn;
}
