using System.Reflection;
using PunchClock.Core.Audit;

namespace PunchClock.Data.Sqlite;

/// <summary>
/// What the schema's <c>pc_ctx(name)</c> function returns on one connection: who is acting, from
/// which client, and why. Audit triggers copy these values into every audit row and refuse the
/// write when the actor or client is missing, so a connection without an actor can read but not write.
/// Mutable: a kiosk unit of work opens before the PIN is checked and sets the actor afterwards.
/// </summary>
public sealed class AuditContext(string client)
{
    /// <summary><c>MACHINE/PunchClock x.y.z</c>, recorded with every audit row.</summary>
    public static string DefaultClient { get; } =
        $"{Environment.MachineName}/PunchClock {typeof(AuditContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0"}";

    public AuditContext(string client, AuditActor actor, string? reason = null)
        : this(client)
    {
        Actor = actor;
        Reason = reason;
    }

    public string Client { get; } = string.IsNullOrWhiteSpace(client)
        ? throw new ArgumentException("Client is required.", nameof(client))
        : client;

    public AuditActor? Actor { get; set; }

    public string? Reason { get; set; }

    /// <summary>The value of <c>pc_ctx(name)</c>. Throws for an unset actor, which aborts the write.</summary>
    internal object? Get(string name) => name switch
    {
        "actor_kind" => (Actor ?? throw NoActor()).Kind switch
        {
            ActorKind.Employee => "employee",
            ActorKind.User => "user",
            var kind => throw new InvalidOperationException($"Unknown actor kind {kind}."),
        },
        "actor_id" => (Actor ?? throw NoActor()).Id,
        "client" => Client,
        "reason" => Reason,
        _ => null,
    };

    private static InvalidOperationException NoActor() =>
        new("pc_ctx: no actor is set on this connection; writes must be attributed to someone.");
}
