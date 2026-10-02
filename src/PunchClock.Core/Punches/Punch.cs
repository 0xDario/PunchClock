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

    /// <summary>Brought in from the legacy Access database.</summary>
    Import,
}

/// <summary>
/// One immutable clock event. Shifts are derived by pairing an In with the next Out;
/// they are never stored, so a punch is never rewritten to "close" a shift.
/// </summary>
/// <param name="OccurredAtUtc">When the employee punched, in UTC.</param>
/// <param name="UtcOffsetMinutes">Site UTC offset at that instant, so reports can show local wall-clock time across DST changes.</param>
/// <param name="RecordedAtUtc">When the row was written. Equal to <paramref name="OccurredAtUtc"/> for live kiosk punches.</param>
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

public sealed record NewPunch(
    long EmployeeId,
    PunchDirection Direction,
    DateTimeOffset OccurredAtUtc,
    int UtcOffsetMinutes,
    DateTimeOffset RecordedAtUtc,
    PunchSource Source);
