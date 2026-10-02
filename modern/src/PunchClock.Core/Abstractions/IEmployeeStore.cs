using PunchClock.Core.Domain;

namespace PunchClock.Core.Abstractions;

public interface IEmployeeStore
{
    IReadOnlyList<Employee> GetActive();

    Employee? Find(long employeeId);

    string? GetPinHash(long employeeId);

    long Add(string firstName, string lastName, string pinHash);

    void SetPinHash(long employeeId, string pinHash);
}
