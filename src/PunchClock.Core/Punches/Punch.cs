namespace PunchClock.Core.Punches;

public enum PunchDirection
{
    In,
    Out,
}

public enum PunchSource
{
    /// <summary>Recorded live by an employee at the kiosk.</summary>
    Kiosk,

    /// <summary>Created by a manager's <c>punch_correction</c>.</summary>
    Correction,

    /// <summary>Brought in from the legacy Access database.</summary>
    LegacyImport,
}

/// <summary>
/// One immutable clock event. Shifts are derived by pairing an In with the next Out;
/// they are never stored, so a punch is never rewritten to "close" a shift.
/// </summary>
/// <param name="OccurredAtUtc">When the punch counts for, in UTC, millisecond precision.</param>
/// <param name="UtcOffsetMinutes">Site UTC offset at that instant, so reports show local wall-clock time across DST changes.</param>
/// <param name="RecordedAtUtc">When the row was written, by the database clock.</param>
public sealed record Punch(
    long Id,
    long EmployeeId,
    PunchDirection Direction,
    DateTimeOffset OccurredAtUtc,
    int UtcOffsetMinutes,
    DateTimeOffset RecordedAtUtc,
    PunchSource Source)
{
    public DateTimeOffset OccurredAtLocal => OccurredAtUtc.ToOffset(TimeSpan.FromMinutes(UtcOffsetMinutes));
}

public enum CorrectionAction
{
    /// <summary>Insert a missing punch.</summary>
    Add,

    /// <summary>Supersede a punch with one carrying new values.</summary>
    Adjust,

    /// <summary>Supersede a punch with nothing.</summary>
    Void,
}

/// <param name="TargetPunchId">Null for <see cref="CorrectionAction.Add"/>.</param>
/// <param name="Reason">Why; at least 10 characters. Kept in the audit log forever.</param>
public sealed record NewCorrection(
    CorrectionAction Action,
    long EmployeeId,
    long? TargetPunchId,
    PunchDirection? NewDirection,
    DateTimeOffset? NewOccurredAtUtc,
    int? NewUtcOffsetMinutes,
    string Reason,
    long ActorUserId);
