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

public sealed class EmployeeService(IPunchClockStore store, IPinHasher pinHasher, TimeProvider clock)
{
    public async Task<IReadOnlyList<Employee>> ListActiveAsync(CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);
        return await uow.ListEmployeesAsync(activeOnly: true, ct);
    }

    public async Task<ClockStatus> GetStatusAsync(long employeeId, CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);
        return ClockState.From(await uow.FindLatestPunchAsync(employeeId, ct));
    }

    /// <exception cref="ArgumentException">A name is blank or the PIN violates <see cref="PinPolicy"/>.</exception>
    public async Task<long> CreateAsync(string firstName, string lastName, string pin, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        if (PinPolicy.Validate(pin) is { } error)
        {
            throw new ArgumentException(error, nameof(pin));
        }

        var employee = new NewEmployee(firstName.Trim(), lastName.Trim(), pinHasher.Hash(pin), clock.GetUtcNow());

        await using var uow = await store.BeginAsync(ct);
        var id = await uow.AddEmployeeAsync(employee, ct);
        await uow.CommitAsync(ct);
        return id;
    }

    public async Task<PinChangeResult> ChangePinAsync(long employeeId, string currentPin, string newPin, CancellationToken ct = default)
    {
        if (PinPolicy.Validate(newPin) is not null)
        {
            return PinChangeResult.NewPinRejected;
        }

        await using var uow = await store.BeginAsync(ct);
        var employee = await uow.FindEmployeeAsync(employeeId, ct);
        if (employee is null)
        {
            return PinChangeResult.EmployeeNotFound;
        }

        if (!pinHasher.Verify(currentPin, employee.PinHash))
        {
            return PinChangeResult.InvalidCurrentPin;
        }

        await uow.SetPinHashAsync(employeeId, pinHasher.Hash(newPin), ct);
        await uow.CommitAsync(ct);
        return PinChangeResult.Changed;
    }
}
