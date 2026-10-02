using PunchClock.Core.Domain;

namespace PunchClock.Core.Abstractions;

public enum AppendPunchStatus
{
    Appended,

    /// <summary>The employee's current state does not allow this direction.</summary>
    Rejected,
}

public sealed record AppendPunchResult(AppendPunchStatus Status, PunchState StateBefore, Punch? Punch);

public interface IPunchStore
{
    Punch? GetLatest(long employeeId);

    IReadOnlyList<Punch> GetForEmployee(long employeeId);

    /// <summary>
    /// Atomically reads the employee's current state, checks it with
    /// <see cref="PunchRules.IsAllowed"/>, and appends the punch only if allowed.
    /// </summary>
    AppendPunchResult TryAppend(NewPunch punch);
}
