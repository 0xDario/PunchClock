using PunchClock.Core.Employees;
using PunchClock.Core.Punches;

namespace PunchClock.Core.Persistence;

/// <summary>Entry point to storage. Every read and write goes through a unit of work.</summary>
public interface IPunchClockStore
{
    /// <summary>
    /// Opens a unit of work that holds the database write lock until disposed.
    /// Nothing is persisted unless <see cref="IPunchClockUnitOfWork.CommitAsync"/> is called.
    /// </summary>
    Task<IPunchClockUnitOfWork> BeginAsync(CancellationToken ct = default);
}

/// <summary>
/// One database transaction. All writes funnel through here, which gives the audit log
/// (designed separately) a single place to record each change in the same transaction.
/// </summary>
public interface IPunchClockUnitOfWork : IAsyncDisposable
{
    Task<Employee?> FindEmployeeAsync(long employeeId, CancellationToken ct = default);

    Task<IReadOnlyList<Employee>> ListEmployeesAsync(bool activeOnly, CancellationToken ct = default);

    Task<long> AddEmployeeAsync(NewEmployee employee, CancellationToken ct = default);

    Task SetPinHashAsync(long employeeId, string pinHash, CancellationToken ct = default);

    /// <summary>Latest punch by <see cref="Punch.OccurredAtUtc"/>, ties broken by insertion order.</summary>
    Task<Punch?> FindLatestPunchAsync(long employeeId, CancellationToken ct = default);

    Task<Punch> AppendPunchAsync(NewPunch punch, CancellationToken ct = default);

    Task CommitAsync(CancellationToken ct = default);
}
