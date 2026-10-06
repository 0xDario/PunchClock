using PunchClock.Core.Audit;
using PunchClock.Core.Persistence;
using PunchClock.Core.Security;
using PunchClock.Core.Site;

namespace PunchClock.Core.Punches;

public enum PunchRejection
{
    EmployeeNotFound,
    EmployeeInactive,
    InvalidPin,

    /// <summary>
    /// The PIN was right but must be replaced first (imported employees: legacy PINs were stored
    /// in plain text). Nothing is recorded until the employee has chosen a new PIN.
    /// </summary>
    PinChangeRequired,

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

    internal static PunchResult Reject(PunchRejection reason, Punch? last = null) => new(null, reason, last);
}

/// <summary>
/// Records punches by explicit intent. The employee says "in" or "out"; the service checks that
/// intent against the current state instead of toggling, so a forgotten punch-out surfaces as
/// <see cref="PunchRejection.AlreadyPunchedIn"/> rather than silently becoming a multi-day shift.
/// The database enforces the same ownership and timing rules again in its triggers.
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

        // Every write from here on is attributed to the employee whose PIN was entered.
        uow.ActAs(AuditActor.ForEmployee(employeeId));

        if (!pinHasher.Verify(pin, employee.PinHash))
        {
            await uow.RecordEventAsync(AuditEvent.AuthPinFailed, ct: ct);
            await uow.CommitAsync(ct);
            return PunchResult.Reject(PunchRejection.InvalidPin);
        }

        if (employee.PinMustChange)
        {
            return PunchResult.Reject(PunchRejection.PinChangeRequired);
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

        var zone = SiteTime.ResolveZone(await uow.GetSettingAsync(SiteSettingKeys.TimeZoneId, ct), clock.LocalTimeZone);
        var offset = (int)zone.GetUtcOffset(now).TotalMinutes;
        var punch = await uow.AppendKioskPunchAsync(employeeId, direction, now, offset, ct);

        await uow.CommitAsync(ct);
        return new PunchResult(punch, null, last);
    }
}
