using PunchClock.Core.Audit;
using PunchClock.Core.Persistence;
using PunchClock.Core.Security;

namespace PunchClock.Core.Accounts;

public enum RecoveryResult
{
    Recovered,
    ReasonRequired,
    PasswordRejected,

    /// <summary>Not a signature of this challenge by a trusted owner key.</summary>
    InvalidResponse,

    /// <summary>Older than <see cref="AdminRecoveryService.ChallengeLifetime"/>; issue a new one.</summary>
    Expired,

    /// <summary>The challenge already recovered the account once.</summary>
    AlreadyUsed,

    /// <summary>The account is no longer an active admin.</summary>
    NotAllowed,
}

/// <summary>
/// Recovers an admin account when no admin can sign in, for example when the only admin forgot their
/// password. The kiosk shows a challenge; the site owner signs it off the PC with their private owner
/// key, and the signed response allows one temporary password, which its owner must replace at next
/// sign-in. Nobody is signed in, so the system account makes the change; its audit row names the
/// owner key, the challenge and the reason. Being at the PC is not enough: without the private key
/// there is no valid response.
/// </summary>
public sealed class AdminRecoveryService(IPunchClockStore store, IPinHasher passwordHasher, IReadOnlyList<string> trustedOwnerKeys)
{
    /// <summary>How long a challenge stays valid, by the database clock.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromHours(1);

    private readonly IReadOnlyList<string> _trustedOwnerKeys = ValidateKeys(trustedOwnerKeys);

    /// <summary>False when this build trusts no owner key, so no admin account can be recovered.</summary>
    public bool IsAvailable => _trustedOwnerKeys.Count > 0;

    /// <summary>Active admin accounts, the only accounts that can be recovered.</summary>
    public async Task<IReadOnlyList<AppUser>> ListRecoverableAsync(CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);
        return [.. (await uow.ListUsersAsync(ct)).Where(IsRecoverable)];
    }

    /// <returns>A new challenge for the account, or null when it is not an active admin account.</returns>
    /// <exception cref="InvalidOperationException">No owner key is trusted.</exception>
    public async Task<RecoveryChallenge?> IssueChallengeAsync(long userId, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("No owner key is configured, so admin accounts cannot be recovered.");
        }

        await using var uow = await store.BeginAsync(ct);
        return await uow.FindUserAsync(userId, ct) is { } user && IsRecoverable(user)
            ? RecoveryChallenge.Create(userId, await uow.GetDatabaseUtcNowAsync(ct))
            : null;
    }

    public async Task<RecoveryResult> RecoverAsync(
        RecoveryChallenge challenge, string response, string temporaryPassword, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (challenge.IsUsed)
        {
            return RecoveryResult.AlreadyUsed;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return RecoveryResult.ReasonRequired;
        }

        if (temporaryPassword is null || temporaryPassword.Length < AccountService.MinPasswordLength)
        {
            return RecoveryResult.PasswordRejected;
        }

        if (OwnerKeySignature.FindSigner(challenge, response ?? "", _trustedOwnerKeys) is not { } ownerKey)
        {
            return RecoveryResult.InvalidResponse;
        }

        // The unit of work holds the write lock, so checking again here stops two calls racing one challenge.
        await using var uow = await store.BeginAsync(ct);
        if (challenge.IsUsed)
        {
            return RecoveryResult.AlreadyUsed;
        }

        if (await uow.GetDatabaseUtcNowAsync(ct) - challenge.IssuedUtc > ChallengeLifetime)
        {
            return RecoveryResult.Expired;
        }

        if (await uow.FindUserAsync(challenge.UserId, ct) is not { } target || !IsRecoverable(target))
        {
            return RecoveryResult.NotAllowed;
        }

        uow.ActAs(AuditActor.System,
            $"Admin password recovery with owner key {OwnerKeySignature.Fingerprint(ownerKey)}, challenge {challenge.Code}: {reason.Trim()}");
        await uow.SetUserPasswordAsync(target.Id, passwordHasher.Hash(temporaryPassword), mustChange: true, ct);
        await uow.CommitAsync(ct);
        challenge.IsUsed = true;
        return RecoveryResult.Recovered;
    }

    private static bool IsRecoverable(AppUser user) => user is { Role: UserRole.Admin, IsActive: true, IsServiceAccount: false };

    private static IReadOnlyList<string> ValidateKeys(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
        {
            // A malformed key would otherwise surface only when someone is locked out.
            OwnerKeySignature.ImportPublicKey(key).Dispose();
        }

        return keys;
    }
}
