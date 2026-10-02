using PunchClock.Core.Abstractions;
using PunchClock.Core.Domain;
using PunchClock.Core.Security;

namespace PunchClock.Core.Services;

public sealed class EmployeeService
{
    private readonly IEmployeeStore _employees;
    private readonly IPinHasher _pinHasher;

    public EmployeeService(IEmployeeStore employees, IPinHasher pinHasher)
    {
        _employees = employees;
        _pinHasher = pinHasher;
    }

    public IReadOnlyList<Employee> GetActive() => _employees.GetActive();

    public long Create(string firstName, string lastName, string pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        return _employees.Add(firstName.Trim(), lastName.Trim(), _pinHasher.Hash(pin));
    }

    /// <summary>Returns false when the current PIN does not match.</summary>
    public bool ChangePin(long employeeId, string currentPin, string newPin)
    {
        var hash = _employees.GetPinHash(employeeId);
        if (hash is null || !PinPolicy.IsValid(currentPin) || !_pinHasher.Verify(currentPin, hash))
        {
            return false;
        }

        _employees.SetPinHash(employeeId, _pinHasher.Hash(newPin));
        return true;
    }
}
