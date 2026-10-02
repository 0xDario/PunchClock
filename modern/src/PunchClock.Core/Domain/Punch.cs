namespace PunchClock.Core.Domain;

/// <summary>
/// A single punch event. Punches are appended, never updated: a shift is the pairing
/// of an In with the following Out. <see cref="OccurredAt"/> keeps the local offset
/// so wall-clock time survives DST changes.
/// </summary>
public sealed record Punch(
    long Id,
    long EmployeeId,
    PunchDirection Direction,
    DateTimeOffset OccurredAt,
    string Source);

public sealed record NewPunch(
    long EmployeeId,
    PunchDirection Direction,
    DateTimeOffset OccurredAt,
    string Source);
