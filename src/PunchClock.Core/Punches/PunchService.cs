using PunchClock.Core.Persistence;
using PunchClock.Core.Security;

namespace PunchClock.Core.Punches;

public enum PunchRejection
{
    EmployeeNotFound,
    EmployeeInactive,
    InvalidPin,

    /// <summary>Asked to punch in while already punched in (often a forgotten punch-out).</summary>
    AlreadyPunchedIn,

    /// <summary>Asked to punch out without an open punch-in.</summary>
    NotPunchedIn,

    /// <summary>The system clock reads earlier than the employee's last punch.</summary>
    ClockBehindLastPunch,
}

/// <param name="Punch">The recorded punch, when accepted.</param>
/// <param name="Rejection">Why nothing was recorded, when rejected.</param>
/// <param name="LastPunch">The employee's latest punch before this attempt, for user-facing context.</param>
public sealed record PunchResult(Punch? Punch, PunchRejection? Rejection, Punch? LastPunch)
{
    public bool Accepted => Punch is not null;

    internal static PunchResult Ok(Punch punch, Punch? last) => new(punch, null, last);

    internal static PunchResult Reject(PunchRejection reason, Punch? last = null) => new(null, reason, last);
}

/// <summary>
/// Records punches by explicit intent. The employee says "in" or "out"; the service checks that
/// intent against the current state instead of toggling, so a forgotten punch-out surfaces as
/// <see cref="PunchRejection.AlreadyPunchedIn"/> rather than silently becoming a multi-day shift.
/// </summary>
public sealed class PunchService(IPunchClockStore store, IPinHasher pinHasher, TimeProvider clock)
{
    public async Task<PunchResult> PunchAsync(long employeeId, string pin, PunchDirection direction, CancellationToken ct = default)
    {
        // The unit of work holds the database write lock, so the state check and the insert are
        // atomic even if two kiosks (or a double-click) race on the same employee.
        await using var uow = await store.BeginAsync(ct);

        var employee = await uow.FindEmployeeAsync(employeeId, ct);
        if (employee is null)
        {
            return PunchResult.Reject(PunchRejection.EmployeeNotFound);
        }

        if (!employee.IsActive)
        {
            return PunchResult.Reject(PunchRejection.EmployeeInactive);
        }

        if (!pinHasher.Verify(pin, employee.PinHash))
        {
            return PunchResult.Reject(PunchRejection.InvalidPin);
        }

        var last = await uow.FindLatestPunchAsync(employeeId, ct);
        var status = ClockState.From(last);

        if (direction == PunchDirection.In && status == ClockStatus.In)
        {
            return PunchResult.Reject(PunchRejection.AlreadyPunchedIn, last);
        }

        if (direction == PunchDirection.Out && status == ClockStatus.Out)
        {
            return PunchResult.Reject(PunchRejection.NotPunchedIn, last);
        }

        var now = clock.GetUtcNow();
        if (last is not null && now < last.OccurredAtUtc)
        {
            return PunchResult.Reject(PunchRejection.ClockBehindLastPunch, last);
        }

        var offset = clock.LocalTimeZone.GetUtcOffset(now);
        var punch = await uow.AppendPunchAsync(
            new NewPunch(employeeId, direction, now, (int)offset.TotalMinutes, now, PunchSource.Kiosk),
            ct);

        await uow.CommitAsync(ct);
        return PunchResult.Ok(punch, last);
    }
}
