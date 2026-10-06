using System.Security.Cryptography;
using System.Text;
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
    ReasonRequired,
    PasswordRejected,
    UsernameTaken,
    EmployeeNotFound,
    EmployeeAlreadyLinked,
}

public enum PasswordChangeResult
{
    Changed,
    InvalidCurrentPassword,
    PasswordRejected,
    TooManyAttempts,
    NotAllowed,
}

/// <summary>Too many failed sign-ins for this username recently; the password was not checked.</summary>
public sealed class SignInLockedException(TimeSpan window) : InvalidOperationException(
    $"Too many failed sign-ins for this account. Try again in {window.TotalMinutes:0} minutes.");

/// <summary>
/// Admin and manager accounts: first-run setup, sign-in, creation, passwords, employee links and
/// activation. Each person gets their own account so the audit log names who made each change.
/// </summary>
public sealed class AccountService(IPunchClockStore store, IPinHasher passwordHasher)
{
    public const int MinPasswordLength = 10;

    /// <summary>
    /// Failed sign-ins allowed per username within <see cref="SignInLockout"/>. A success, or an
    /// admin changing the account, clears the count; otherwise it ages out of the window.
    /// </summary>
    public const int MaxFailedSignIns = 5;

    public static readonly TimeSpan SignInLockout = TimeSpan.FromMinutes(15);

    private static readonly byte[] UsernameDigestSalt = Encoding.UTF8.GetBytes("PunchClock sign-in username v1");

    // Checked when the username is unknown or cannot sign in, so every failure costs one full
    // password verification and the response time does not reveal which usernames exist.
    private readonly Lazy<string> _decoyHash = new(() => passwordHasher.Hash(Guid.NewGuid().ToString()));

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
    /// <exception cref="SignInLockedException">Too many recent failures for this username.</exception>
    public async Task<AppUser?> SignInAsync(string username, string password, CancellationToken ct = default)
    {
        username = username.Trim();
        var digest = UsernameDigest(username);
        await using var uow = await store.BeginAsync(ct);
        var user = await uow.FindUserByUsernameAsync(username, ct);

        // Checked before the password, for unknown usernames too, so a locked guess learns nothing.
        if (await uow.CountRecentSignInFailuresAsync(digest, user?.Id, SignInLockout, ct) >= MaxFailedSignIns)
        {
            throw new SignInLockedException(SignInLockout);
        }

        var signer = user is { IsActive: true, IsServiceAccount: false, PasswordHash: not null } ? user : null;
        if (passwordHasher.Verify(password, signer?.PasswordHash ?? _decoyHash.Value) && signer is not null)
        {
            uow.ActAs(AuditActor.ForUser(signer.Id));
            await uow.RecordEventAsync(AuditEvent.AuthLogin, ct: ct);
            await uow.CommitAsync(ct);
            return signer;
        }

        // Nobody is authenticated, so the system account records the attempt. What was typed is
        // kept only when it names an account: people type passwords into the username box, and
        // the log is permanent. The digest still lets unknown names lock out like real ones.
        uow.ActAs(AuditActor.System);
        await uow.RecordEventAsync(AuditEvent.AuthLoginFailed, FailureDetail(user?.Username, digest), ct);
        await uow.CommitAsync(ct);
        return null;
    }

    /// <summary>
    /// Creates a named admin or manager account with a temporary password the owner must replace
    /// at first sign-in. <paramref name="employeeId"/> links the employee the person also punches
    /// as, so they cannot correct their own punches. Admins only.
    /// </summary>
    public async Task<AccountChangeResult> CreateAccountAsync(
        AppUser admin, string username, string displayName, UserRole role, string temporaryPassword, long? employeeId,
        CancellationToken ct = default)
    {
        if (!IsActiveAdmin(admin) || role is not (UserRole.Admin or UserRole.Manager)
            || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(displayName))
        {
            return AccountChangeResult.NotAllowed;
        }

        if (!IsAcceptablePassword(temporaryPassword))
        {
            return AccountChangeResult.PasswordRejected;
        }

        await using var uow = await store.BeginAsync(ct);
        if (await uow.FindUserByUsernameAsync(username.Trim(), ct) is not null)
        {
            return AccountChangeResult.UsernameTaken;
        }

        if (await CheckEmployeeLinkAsync(uow, userId: null, employeeId, ct) is { } refused)
        {
            return refused;
        }

        uow.ActAs(AuditActor.ForUser(admin.Id));
        await uow.AddUserAsync(new NewAppUser(
            username.Trim(), displayName.Trim(), role, passwordHasher.Hash(temporaryPassword), employeeId, MustChangePassword: true), ct);
        await uow.CommitAsync(ct);
        return AccountChangeResult.Changed;
    }

    /// <summary>
    /// The signed-in person replaces their own password, which also clears a forced change.
    /// A wrong current password counts as a failed sign-in for the lockout.
    /// </summary>
    public async Task<PasswordChangeResult> ChangeOwnPasswordAsync(
        AppUser user, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);
        var current = await uow.FindUserAsync(user.Id, ct);
        if (current is not { IsActive: true, IsServiceAccount: false, PasswordHash: { } hash })
        {
            return PasswordChangeResult.NotAllowed;
        }

        var digest = UsernameDigest(current.Username);
        if (await uow.CountRecentSignInFailuresAsync(digest, current.Id, SignInLockout, ct) >= MaxFailedSignIns)
        {
            return PasswordChangeResult.TooManyAttempts;
        }

        uow.ActAs(AuditActor.ForUser(current.Id));
        if (!passwordHasher.Verify(currentPassword, hash))
        {
            await uow.RecordEventAsync(AuditEvent.AuthLoginFailed, FailureDetail(current.Username, digest), ct);
            await uow.CommitAsync(ct);
            return PasswordChangeResult.InvalidCurrentPassword;
        }

        if (!IsAcceptablePassword(newPassword) || newPassword == currentPassword)
        {
            return PasswordChangeResult.PasswordRejected;
        }

        await uow.SetUserPasswordAsync(current.Id, passwordHasher.Hash(newPassword), mustChange: false, ct);
        await uow.CommitAsync(ct);
        return PasswordChangeResult.Changed;
    }

    /// <summary>
    /// An admin sets a temporary password for someone who forgot theirs; the owner must replace it
    /// at their next sign-in. Clears that account's sign-in lockout. Not for the admin's own account.
    /// </summary>
    public async Task<AccountChangeResult> ResetPasswordAsync(
        AppUser admin, long userId, string temporaryPassword, string reason, CancellationToken ct = default)
    {
        if (!IsActiveAdmin(admin) || userId == admin.Id)
        {
            return AccountChangeResult.NotAllowed;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return AccountChangeResult.ReasonRequired;
        }

        if (!IsAcceptablePassword(temporaryPassword))
        {
            return AccountChangeResult.PasswordRejected;
        }

        await using var uow = await store.BeginAsync(ct);
        var target = await uow.FindUserAsync(userId, ct);
        if (target is null)
        {
            return AccountChangeResult.NotFound;
        }

        if (target.IsServiceAccount)
        {
            return AccountChangeResult.NotAllowed;
        }

        uow.ActAs(AuditActor.ForUser(admin.Id), reason.Trim());
        await uow.SetUserPasswordAsync(userId, passwordHasher.Hash(temporaryPassword), mustChange: true, ct);
        await uow.CommitAsync(ct);
        return AccountChangeResult.Changed;
    }

    /// <summary>
    /// Links an account to the employee its owner punches as, or unlinks it (null). An admin cannot
    /// change their own link, so nobody can unlink themselves to correct their own punches.
    /// </summary>
    public async Task<AccountChangeResult> SetEmployeeLinkAsync(
        AppUser admin, long userId, long? employeeId, string reason, CancellationToken ct = default)
    {
        if (!IsActiveAdmin(admin) || userId == admin.Id)
        {
            return AccountChangeResult.NotAllowed;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return AccountChangeResult.ReasonRequired;
        }

        await using var uow = await store.BeginAsync(ct);
        var target = await uow.FindUserAsync(userId, ct);
        if (target is null)
        {
            return AccountChangeResult.NotFound;
        }

        if (target.IsServiceAccount)
        {
            return AccountChangeResult.NotAllowed;
        }

        if (await CheckEmployeeLinkAsync(uow, userId, employeeId, ct) is { } refused)
        {
            return refused;
        }

        uow.ActAs(AuditActor.ForUser(admin.Id), reason.Trim());
        await uow.SetUserEmployeeAsync(userId, employeeId, ct);
        await uow.CommitAsync(ct);
        return AccountChangeResult.Changed;
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

    /// <summary>
    /// Lockout key for a typed username: case-insensitive, and a slow salted hash so a username box
    /// that received a password does not leave it readable, or cheaply guessable, in the audit log.
    /// </summary>
    internal static string UsernameDigest(string username) => Convert.ToHexStringLower(Rfc2898DeriveBytes.Pbkdf2(
        Encoding.UTF8.GetBytes(username.Trim().ToLowerInvariant()), UsernameDigestSalt, 100_000, HashAlgorithmName.SHA256, 32));

    private static string FailureDetail(string? knownUsername, string digest) => knownUsername is null
        ? JsonSerializer.Serialize(new { username_digest = digest })
        : JsonSerializer.Serialize(new { username = knownUsername, username_digest = digest });

    private static bool IsActiveAdmin(AppUser user) => user is { Role: UserRole.Admin, IsActive: true };

    private static bool IsAcceptablePassword(string? password) => password is not null && password.Length >= MinPasswordLength;

    private static async Task<AccountChangeResult?> CheckEmployeeLinkAsync(
        IPunchClockUnitOfWork uow, long? userId, long? employeeId, CancellationToken ct)
    {
        if (employeeId is not { } id)
        {
            return null;
        }

        if (await uow.FindEmployeeAsync(id, ct) is null)
        {
            return AccountChangeResult.EmployeeNotFound;
        }

        return (await uow.ListUsersAsync(ct)).Any(u => u.EmployeeId == id && u.Id != userId)
            ? AccountChangeResult.EmployeeAlreadyLinked
            : null;
    }
}
