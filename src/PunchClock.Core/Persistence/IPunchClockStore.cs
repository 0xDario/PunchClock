using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;
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
/// One database transaction. Reads need no actor. Every write requires <see cref="ActAs"/> first:
/// the database's audit triggers attribute each change to that actor, check that the actor is
/// allowed to make it, and reject the write outright when no actor is set.
/// </summary>
public interface IPunchClockUnitOfWork : IAsyncDisposable
{
    /// <summary>Sets who subsequent writes are attributed to, and an optional reason stored with them.</summary>
    void ActAs(AuditActor actor, string? reason = null);

    Task RecordEventAsync(AuditEvent auditEvent, string? detailJson = null, CancellationToken ct = default);

    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);

    Task SetSettingAsync(string key, string value, CancellationToken ct = default);

    Task<Employee?> FindEmployeeAsync(long employeeId, CancellationToken ct = default);

    Task<IReadOnlyList<Employee>> ListEmployeesAsync(bool activeOnly, CancellationToken ct = default);

    Task<long> AddEmployeeAsync(NewEmployee employee, CancellationToken ct = default);

    /// <summary>Wrong-PIN attempts for the employee within <paramref name="window"/> that no later success or reset has cleared.</summary>
    Task<int> CountRecentPinFailuresAsync(long employeeId, TimeSpan window, CancellationToken ct = default);

    /// <summary>
    /// Failed sign-ins recorded with <paramref name="usernameDigest"/> within <paramref name="window"/>
    /// that no later sign-in by, or change to, account <paramref name="userId"/> has cleared.
    /// </summary>
    Task<int> CountRecentSignInFailuresAsync(string usernameDigest, long? userId, TimeSpan window, CancellationToken ct = default);

    /// <summary>Sets a new PIN hash and <see cref="Employee.PinMustChange"/>.</summary>
    Task SetPinHashAsync(long employeeId, string pinHash, bool mustChange, CancellationToken ct = default);

    Task SetEmployeeActiveAsync(long employeeId, bool isActive, CancellationToken ct = default);

    /// <summary>
    /// The database clock, to the millisecond: the clock the schema stamps <c>recorded_utc</c> and
    /// audit times with. Kiosk punch times come from here, not the .NET clock: on Windows SQLite reads
    /// a coarser system clock than <see cref="DateTimeOffset.UtcNow"/>, so an app-side "now" can be a few
    /// milliseconds ahead of the database's and the schema refuses it as a future punch.
    /// </summary>
    Task<DateTimeOffset> GetDatabaseUtcNowAsync(CancellationToken ct = default);

    /// <summary>Latest effective (not superseded) punch by time, ties broken by insertion order.</summary>
    Task<Punch?> FindLatestPunchAsync(long employeeId, CancellationToken ct = default);

    Task<Punch> AppendKioskPunchAsync(long employeeId, PunchDirection direction, DateTimeOffset occurredAtUtc, int utcOffsetMinutes, CancellationToken ct = default);

    /// <summary>The punch if it exists and no correction has superseded it.</summary>
    Task<Punch?> FindEffectivePunchAsync(long punchId, CancellationToken ct = default);

    /// <summary>Effective punches with <paramref name="fromUtc"/> &lt;= time &lt; <paramref name="toUtc"/>, oldest first.</summary>
    Task<IReadOnlyList<Punch>> ListEffectivePunchesAsync(long employeeId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);

    /// <summary>The schema's review queue (<c>punch_exception_v</c>) for one employee: punch id and kind.</summary>
    Task<IReadOnlyList<(long PunchId, string Kind)>> ListPunchExceptionsAsync(long employeeId, CancellationToken ct = default);

    Task<long> AddCorrectionAsync(NewCorrection correction, CancellationToken ct = default);

    /// <summary>Shifts (<c>shift_v</c>) of every employee whose IN is in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>).</summary>
    Task<IReadOnlyList<Reports.ShiftRecord>> ListShiftsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);

    /// <summary>Every punch, superseded ones included, with time in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>).</summary>
    Task<IReadOnlyList<Reports.PunchExportRow>> ListPunchesForExportAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);

    /// <summary>Corrections made in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>).</summary>
    Task<IReadOnlyList<Reports.CorrectionExportRow>> ListCorrectionsForExportAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);

    Task<AppUser?> FindUserAsync(long userId, CancellationToken ct = default);

    Task<AppUser?> FindUserByUsernameAsync(string username, CancellationToken ct = default);

    Task<IReadOnlyList<AppUser>> ListUsersAsync(CancellationToken ct = default);

    Task<long> AddUserAsync(NewAppUser user, CancellationToken ct = default);

    Task SetUserActiveAsync(long userId, bool isActive, CancellationToken ct = default);

    Task SetUserPasswordAsync(long userId, string passwordHash, bool mustChange, CancellationToken ct = default);

    /// <summary>Links the account to the employee its owner punches as, or unlinks it (null).</summary>
    Task SetUserEmployeeAsync(long userId, long? employeeId, CancellationToken ct = default);

    Task CommitAsync(CancellationToken ct = default);
}
