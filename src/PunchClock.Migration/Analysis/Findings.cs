namespace PunchClock.Migration.Analysis;

/// <summary>
/// Something in the legacy data a manager should know about. Findings never
/// change what gets imported: every legacy row is carried over as recorded,
/// and fixes go through the new app's audited correction path afterwards.
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
    PlaceholderEmployee,
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
        FindingCode.DummyShift => "Zero-length shift the old app created with each new employee",
        FindingCode.ZeroLengthShift => "Zero-length shift (punch in and out at the same instant)",
        FindingCode.OpenShiftCurrent => "Employee is punched in right now (latest shift has no punch out)",
        FindingCode.OpenShiftStale => "Missed punch out: shift never closed, later shifts exist",
        FindingCode.OrphanShift => "Shift belongs to an employee ID that does not exist",
        FindingCode.MissingEmployeeId => "Shift has no employee ID",
        FindingCode.MissingTimeIn => "Shift has no punch-in time",
        FindingCode.NegativeShift => "Punch out is before punch in",
        FindingCode.LongShift => "Shift longer than 16 hours",
        FindingCode.OverlappingShift => "Shift starts before the employee's previous shift ended",
        FindingCode.OutOfOrderShiftId => "Shift was added out of time order (likely edited directly in Access)",
        FindingCode.DstAmbiguousTime => "Time falls in the repeated hour when clocks go back",
        FindingCode.DstNonexistentTime => "Time falls in the skipped hour when clocks go forward",
        FindingCode.CrossesDstChange => "Shift spans a daylight-saving change; real hours differ from the old report",
        FindingCode.PlaceholderEmployee => "Placeholder employee created to hold orphan shifts",
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
}
