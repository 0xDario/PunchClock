namespace PunchClock.Core.Audit;

public enum ActorKind
{
    /// <summary>An employee, identified by PIN at the kiosk.</summary>
    Employee,

    /// <summary>An <c>app_user</c>: a signed-in admin or manager, or a service account.</summary>
    User,
}

/// <summary>
/// Who a write is attributed to. The database refuses any write whose audit row would name a
/// different actor than the one set on the connection, and refuses every write when none is set.
/// </summary>
public readonly record struct AuditActor(ActorKind Kind, long Id)
{
    /// <summary>The built-in account (app_user 1) for bootstrap and schema migrations.</summary>
    public static AuditActor System { get; } = new(ActorKind.User, 1);

    /// <summary>The legacy importer's account (app_user 2).</summary>
    public static AuditActor Migration { get; } = new(ActorKind.User, 2);

    public static AuditActor ForEmployee(long employeeId) => new(ActorKind.Employee, employeeId);

    public static AuditActor ForUser(long userId) => new(ActorKind.User, userId);
}

/// <summary>Audit events that are not row changes; row changes are logged by the database itself.</summary>
public enum AuditEvent
{
    AuthLogin,
    AuthLoginFailed,
    AuthLogout,
    AuthPinFailed,
    AppStart,
    ReportExport,
    Backup,
}
