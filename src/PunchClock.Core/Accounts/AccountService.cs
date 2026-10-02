using System.Text.Json;
using PunchClock.Core.Audit;
using PunchClock.Core.Persistence;
using PunchClock.Core.Security;

namespace PunchClock.Core.Accounts;

public enum AccountChangeResult
{
    Changed,
    NotFound,
    NotAllowed,
}

/// <summary>Admin and manager accounts: first-run setup, sign-in, activation.</summary>
public sealed class AccountService(IPunchClockStore store, IPinHasher passwordHasher)
{
    public const int MinPasswordLength = 10;

    /// <summary>True until the first admin exists; only then may <see cref="CreateFirstAdminAsync"/> run.</summary>
    public async Task<bool> NeedsFirstAdminAsync(CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);
        return !(await uow.ListUsersAsync(ct)).Any(u => u.Role == UserRole.Admin);
    }

    /// <summary>
    /// Creates the first admin, attributed to the system account. Refused once any admin exists,
    /// so this cannot be used to add a second admin from the kiosk.
    /// </summary>
    /// <exception cref="ArgumentException">Blank username or display name, or a password shorter than <see cref="MinPasswordLength"/>.</exception>
    public async Task<AppUser> CreateFirstAdminAsync(string username, string displayName, string password, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (password is null || password.Length < MinPasswordLength)
        {
            throw new ArgumentException($"Password must be at least {MinPasswordLength} characters.", nameof(password));
        }

        await using var uow = await store.BeginAsync(ct);
        if ((await uow.ListUsersAsync(ct)).Any(u => u.Role == UserRole.Admin))
        {
            throw new InvalidOperationException("An admin account already exists.");
        }

        uow.ActAs(AuditActor.System, "first-run admin setup");
        var id = await uow.AddUserAsync(new NewAppUser(username.Trim(), displayName.Trim(), UserRole.Admin, passwordHasher.Hash(password)), ct);
        var admin = (await uow.FindUserAsync(id, ct))!;

        // Creating the account signs its owner in.
        uow.ActAs(AuditActor.ForUser(id));
        await uow.RecordEventAsync(AuditEvent.AuthLogin, ct: ct);
        await uow.CommitAsync(ct);
        return admin;
    }

    /// <returns>The signed-in admin or manager, or null. Both outcomes are audited.</returns>
    public async Task<AppUser?> SignInAsync(string username, string password, CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);
        var user = await uow.FindUserByUsernameAsync(username.Trim(), ct);

        if (user is { IsActive: true, IsServiceAccount: false, PasswordHash: { } hash }
            && passwordHasher.Verify(password, hash))
        {
            uow.ActAs(AuditActor.ForUser(user.Id));
            await uow.RecordEventAsync(AuditEvent.AuthLogin, ct: ct);
            await uow.CommitAsync(ct);
            return user;
        }

        // Nobody is authenticated, so the system account records the attempt.
        uow.ActAs(AuditActor.System);
        await uow.RecordEventAsync(AuditEvent.AuthLoginFailed, JsonSerializer.Serialize(new { username }), ct);
        await uow.CommitAsync(ct);
        return null;
    }

    public async Task SignOutAsync(AppUser user, CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);
        uow.ActAs(AuditActor.ForUser(user.Id));
        await uow.RecordEventAsync(AuditEvent.AuthLogout, ct: ct);
        await uow.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);
        return await uow.ListUsersAsync(ct);
    }

    /// <summary>
    /// Activates or deactivates an account. The system account and the caller's own account are
    /// refused: schema migrations run as system, and an admin locking themselves out needs a second admin.
    /// </summary>
    public async Task<AccountChangeResult> SetActiveAsync(AppUser admin, long userId, bool isActive, string reason, CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);
        var target = await uow.FindUserAsync(userId, ct);
        if (target is null)
        {
            return AccountChangeResult.NotFound;
        }

        if (target.Role == UserRole.System || target.Id == admin.Id)
        {
            return AccountChangeResult.NotAllowed;
        }

        uow.ActAs(AuditActor.ForUser(admin.Id), reason);
        await uow.SetUserActiveAsync(userId, isActive, ct);
        await uow.CommitAsync(ct);
        return AccountChangeResult.Changed;
    }
}
