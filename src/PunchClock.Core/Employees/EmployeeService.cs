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
    /// <exception cref="ArgumentException">A name is blank or the PIN violates <see cref="PinPolicy"/>.</exception>
    public async Task<long> CreateAsync(AuditActor by, string firstName, string lastName, string pin, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        if (PinPolicy.Validate(pin) is { } error)
        {
            throw new ArgumentException(error, nameof(pin));
        }

        var employee = new NewEmployee(firstName.Trim(), lastName.Trim(), pinHasher.Hash(pin));

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

    /// <summary>An employee replaces their own PIN; also clears a forced PIN change.</summary>
    public async Task<PinChangeResult> ChangeOwnPinAsync(long employeeId, string currentPin, string newPin, CancellationToken ct = default)
    {
        if (PinPolicy.Validate(newPin) is not null)
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
        if (!pinHasher.Verify(currentPin, employee.PinHash))
        {
            await uow.RecordEventAsync(AuditEvent.AuthPinFailed, ct: ct);
            await uow.CommitAsync(ct);
            return PinChangeResult.InvalidCurrentPin;
        }

        await uow.SetPinHashAsync(employeeId, pinHasher.Hash(newPin), ct);
        await uow.CommitAsync(ct);
        return PinChangeResult.Changed;
    }
}
