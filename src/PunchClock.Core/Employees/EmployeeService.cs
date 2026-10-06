using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;
using PunchClock.Core.Persistence;
using PunchClock.Core.Punches;
using PunchClock.Core.Security;

namespace PunchClock.Core.Employees;

public enum PinChangeResult
{
    Changed,
    EmployeeNotFound,
    InvalidCurrentPin,
    NewPinRejected,

    /// <summary>Too many wrong PINs recently; see <see cref="PinPolicy.MaxFailedAttempts"/>.</summary>
    TooManyAttempts,
}

public enum PinResetResult
{
    Reset,
    EmployeeNotFound,

    /// <summary>Only managers and admins reset PINs, and only with a reason.</summary>
    NotAllowed,
    PinRejected,
}

public sealed class EmployeeService(IPunchClockStore store, IPinHasher pinHasher)
{
    public async Task<IReadOnlyList<Employee>> ListAsync(bool activeOnly, CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);
        return await uow.ListEmployeesAsync(activeOnly, ct);
    }

    public Task<IReadOnlyList<Employee>> ListActiveAsync(CancellationToken ct = default) => ListAsync(activeOnly: true, ct);

    public async Task<ClockStatus> GetStatusAsync(long employeeId, CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);
        return ClockState.From(await uow.FindLatestPunchAsync(employeeId, ct));
    }

    /// <param name="by">A manager, an admin or the system account; the database rejects anyone else.</param>
    /// <param name="pinMustChange">
    /// True unless the employee chose <paramref name="pin"/> themselves: whoever typed it in knows it,
    /// so by default the employee must replace it before their first punch is recorded.
    /// </param>
    /// <exception cref="ArgumentException">A name is blank or the PIN violates <see cref="PinPolicy"/>.</exception>
    public async Task<long> CreateAsync(
        AuditActor by, string firstName, string lastName, string pin, bool pinMustChange = true, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        if (PinPolicy.Validate(pin) is { } error)
        {
            throw new ArgumentException(error, nameof(pin));
        }

        var employee = new NewEmployee(firstName.Trim(), lastName.Trim(), pinHasher.Hash(pin), pinMustChange);

        await using var uow = await store.BeginAsync(ct);
        uow.ActAs(by);
        var id = await uow.AddEmployeeAsync(employee, ct);
        await uow.CommitAsync(ct);
        return id;
    }

    public async Task SetActiveAsync(AuditActor by, long employeeId, bool isActive, CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);
        uow.ActAs(by);
        await uow.SetEmployeeActiveAsync(employeeId, isActive, ct);
        await uow.CommitAsync(ct);
    }

    /// <summary>
    /// An employee replaces their own PIN; also clears a forced PIN change. The new PIN must differ,
    /// so a temporary or legacy PIN someone else knows cannot simply be kept.
    /// </summary>
    public async Task<PinChangeResult> ChangeOwnPinAsync(long employeeId, string currentPin, string newPin, CancellationToken ct = default)
    {
        if (PinPolicy.Validate(newPin) is not null || newPin == currentPin)
        {
            return PinChangeResult.NewPinRejected;
        }

        await using var uow = await store.BeginAsync(ct);
        var employee = await uow.FindEmployeeAsync(employeeId, ct);
        if (employee is null || !employee.IsActive)
        {
            return PinChangeResult.EmployeeNotFound;
        }

        uow.ActAs(AuditActor.ForEmployee(employeeId));
        if (await uow.CountRecentPinFailuresAsync(employeeId, PinPolicy.LockoutWindow, ct) >= PinPolicy.MaxFailedAttempts)
        {
            return PinChangeResult.TooManyAttempts;
        }

        if (!pinHasher.Verify(currentPin, employee.PinHash))
        {
            await uow.RecordEventAsync(AuditEvent.AuthPinFailed, ct: ct);
            await uow.CommitAsync(ct);
            return PinChangeResult.InvalidCurrentPin;
        }

        await uow.SetPinHashAsync(employeeId, pinHasher.Hash(newPin), mustChange: false, ct);
        await uow.CommitAsync(ct);
        return PinChangeResult.Changed;
    }

    /// <summary>
    /// A manager or admin gives an employee who forgot their PIN a temporary one. The manager knows
    /// it, so the employee must replace it before their next punch is recorded; the reset is
    /// audited under the manager's account with <paramref name="reason"/>.
    /// </summary>
    public async Task<PinResetResult> ResetPinAsync(
        AppUser by, long employeeId, string temporaryPin, string reason, CancellationToken ct = default)
    {
        if (by.Role is not (UserRole.Admin or UserRole.Manager) || string.IsNullOrWhiteSpace(reason))
        {
            return PinResetResult.NotAllowed;
        }

        if (PinPolicy.Validate(temporaryPin) is not null)
        {
            return PinResetResult.PinRejected;
        }

        await using var uow = await store.BeginAsync(ct);
        var employee = await uow.FindEmployeeAsync(employeeId, ct);
        if (employee is null || !employee.IsActive)
        {
            return PinResetResult.EmployeeNotFound;
        }

        uow.ActAs(AuditActor.ForUser(by.Id), reason.Trim());
        await uow.SetPinHashAsync(employeeId, pinHasher.Hash(temporaryPin), mustChange: true, ct);
        await uow.CommitAsync(ct);
        return PinResetResult.Reset;
    }
}
