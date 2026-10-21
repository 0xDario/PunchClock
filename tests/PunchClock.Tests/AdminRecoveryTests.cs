using System.Security.Cryptography;
using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;

namespace PunchClock.Tests;

public sealed class AdminRecoveryTests : DatabaseTest
{
    private const string Temporary = "temporary pass 1";

    private readonly ECDsa _ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private AdminRecoveryService Recovery => new(Db.Store, TestDatabase.FastHasher, [OwnerKeySignature.ExportPublicKey(_ownerKey)]);

    [Fact]
    public async Task The_owner_key_recovers_a_locked_out_admin_with_an_audited_temporary_password()
    {
        var admin = await Db.AddAdminAsync();
        for (var i = 0; i < AccountService.MaxFailedSignIns; i++)
        {
            await Db.Accounts.SignInAsync(admin.Username, $"guess number {i}");
        }

        await Assert.ThrowsAsync<SignInLockedException>(() => Db.Accounts.SignInAsync(admin.Username, "correct horse battery"));

        var recovery = Recovery;
        Assert.True(recovery.IsAvailable);
        Assert.Equal([admin.Id], (await recovery.ListRecoverableAsync()).Select(u => u.Id));
        var challenge = (await recovery.IssueChallengeAsync(admin.Id))!;
        Assert.Equal(admin.Id, challenge.UserId);

        // The owner's tool reads the code as typed, in any case and grouping, and signs it.
        var response = OwnerKeySignature.Sign(_ownerKey, RecoveryChallenge.Parse(challenge.Code.Replace("-", " ").ToLowerInvariant()));

        Assert.Equal(RecoveryResult.ReasonRequired, await recovery.RecoverAsync(challenge, response, Temporary, " "));
        Assert.Equal(RecoveryResult.PasswordRejected, await recovery.RecoverAsync(challenge, response, "short", "Forgot password"));
        Assert.Equal(RecoveryResult.Recovered, await recovery.RecoverAsync(challenge, response.ToLowerInvariant(), Temporary, " Forgot password "));
        Assert.True(challenge.IsUsed);
        Assert.Equal(RecoveryResult.AlreadyUsed, await recovery.RecoverAsync(challenge, response, "another temporary", "Again"));

        var recovered = (await Db.Accounts.SignInAsync(admin.Username, Temporary))!;
        Assert.True(recovered.MustChangePassword);
        Assert.Equal(PasswordChangeResult.Changed, await Db.Accounts.ChangeOwnPasswordAsync(recovered, Temporary, "a new passphrase"));

        var fingerprint = OwnerKeySignature.Fingerprint(OwnerKeySignature.ExportPublicKey(_ownerKey));
        Assert.Equal(1, await Db.ScalarAsync<long>(
            """
            SELECT count(*) FROM audit_log
             WHERE table_name = 'app_user' AND action = 'UPDATE' AND row_id = $id AND actor_kind = 'user' AND actor_id = $system
               AND reason = $reason;
            """,
            ("$id", admin.Id), ("$system", AuditActor.System.Id),
            ("$reason", $"Admin password recovery with owner key {fingerprint}, challenge {challenge.Code}: Forgot password")));
        Assert.Empty(await Db.VerifyAsync());
    }

    [Fact]
    public async Task Only_the_owner_keys_signature_of_this_challenge_is_accepted()
    {
        var admin = await Db.AddAdminAsync();
        var recovery = Recovery;
        var challenge = (await recovery.IssueChallengeAsync(admin.Id))!;
        var other = (await recovery.IssueChallengeAsync(admin.Id))!;
        Assert.NotEqual(challenge.Code, other.Code);

        var signed = OwnerKeySignature.Sign(_ownerKey, challenge);
        var tampered = signed[..^2] + (signed[^2] == '0' ? '1' : '0') + signed[^1];
        foreach (var response in new[]
                 {
                     OwnerKeySignature.Sign(_otherKey, challenge),
                     OwnerKeySignature.Sign(_ownerKey, other),
                     tampered,
                     signed[..^5],
                     "",
                     "not a response!",
                 })
        {
            Assert.Equal(RecoveryResult.InvalidResponse, await recovery.RecoverAsync(challenge, response, Temporary, "Forgot password"));
        }

        Assert.False(challenge.IsUsed);
        Assert.Null(await Db.Accounts.SignInAsync(admin.Username, Temporary));
        Assert.Equal(0, await Db.ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE table_name = 'app_user' AND action = 'UPDATE';"));
    }

    [Fact]
    public async Task A_challenge_expires_after_its_lifetime()
    {
        var admin = await Db.AddAdminAsync();
        var recovery = Recovery;
        var now = (await recovery.IssueChallengeAsync(admin.Id))!.IssuedUtc;
        var stale = RecoveryChallenge.Create(admin.Id, now - AdminRecoveryService.ChallengeLifetime - TimeSpan.FromSeconds(5));
        var fresh = RecoveryChallenge.Create(admin.Id, now - AdminRecoveryService.ChallengeLifetime + TimeSpan.FromMinutes(5));

        Assert.Equal(RecoveryResult.Expired, await recovery.RecoverAsync(stale, OwnerKeySignature.Sign(_ownerKey, stale), Temporary, "Forgot password"));
        Assert.Equal(RecoveryResult.Recovered, await recovery.RecoverAsync(fresh, OwnerKeySignature.Sign(_ownerKey, fresh), Temporary, "Forgot password"));
    }

    [Fact]
    public async Task Only_active_admin_accounts_can_be_recovered()
    {
        var manager = await Db.AddManagerAsync();
        var admin = (await Db.Accounts.ListAsync()).Single(u => u.Username == "admin-for-manager");
        var second = await Db.AddAdminAsync("second");
        var recovery = Recovery;

        Assert.Null(await recovery.IssueChallengeAsync(manager.Id));
        Assert.Null(await recovery.IssueChallengeAsync(AuditActor.System.Id));
        Assert.Null(await recovery.IssueChallengeAsync(AuditActor.Migration.Id));
        Assert.Null(await recovery.IssueChallengeAsync(9999));

        // Deactivated after the challenge was issued: the signed response no longer helps.
        var challenge = (await recovery.IssueChallengeAsync(second.Id))!;
        Assert.Equal(AccountChangeResult.Changed, await Db.Accounts.SetActiveAsync(admin, second.Id, false, "Left the company"));
        Assert.Equal(RecoveryResult.NotAllowed, await recovery.RecoverAsync(challenge, OwnerKeySignature.Sign(_ownerKey, challenge), Temporary, "Forgot password"));
        Assert.Equal([admin.Id], (await recovery.ListRecoverableAsync()).Select(u => u.Id));
        Assert.Empty(await Db.VerifyAsync());
    }

    [Fact]
    public async Task Without_a_trusted_owner_key_nothing_can_be_recovered()
    {
        var admin = await Db.AddAdminAsync();
        var recovery = new AdminRecoveryService(Db.Store, TestDatabase.FastHasher, []);

        Assert.False(recovery.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.IssueChallengeAsync(admin.Id));
        var challenge = RecoveryChallenge.Create(admin.Id, DateTimeOffset.UtcNow);
        Assert.Equal(RecoveryResult.InvalidResponse, await recovery.RecoverAsync(challenge, OwnerKeySignature.Sign(_ownerKey, challenge), Temporary, "Forgot password"));
    }

    [Fact]
    public void Malformed_or_wrong_curve_owner_keys_are_refused_at_startup()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);
        foreach (var key in new[] { "not base64", Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), OwnerKeySignature.ExportPublicKey(p384) })
        {
            Assert.Throws<ArgumentException>(() => new AdminRecoveryService(Db.Store, TestDatabase.FastHasher, [key]));
        }
    }

    [Fact]
    public void Shipped_owner_keys_are_valid()
    {
        // The kiosk would refuse to start with a malformed key, and with none it could not recover an admin.
        var recovery = new AdminRecoveryService(Db.Store, TestDatabase.FastHasher, OwnerKeys.Trusted);
        Assert.True(recovery.IsAvailable);
        Assert.Contains("a69cf00996d01267", OwnerKeys.Trusted.Select(OwnerKeySignature.Fingerprint));
    }

    [Fact]
    public void Codes_round_trip_and_reject_mistypes()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        var code = OwnerKeyCode.Encode(bytes);
        Assert.Equal(103, code.Length);
        Assert.Equal(bytes, OwnerKeyCode.Decode(OwnerKeyCode.Group(code, 5).ToLowerInvariant()));
        Assert.Equal(OwnerKeyCode.Decode("10"), OwnerKeyCode.Decode("Lo"));
        Assert.Throws<FormatException>(() => OwnerKeyCode.Decode(code[..^1]));
        Assert.Throws<FormatException>(() => OwnerKeyCode.Decode("U"));

        var challenge = RecoveryChallenge.Create(42, DateTimeOffset.FromUnixTimeSeconds(1_792_000_000));
        Assert.Matches("^([0-9A-Z]{4}-){7}[0-9A-Z]{4}$", challenge.Code);
        var parsed = RecoveryChallenge.Parse(challenge.Code);
        Assert.Equal((42L, challenge.IssuedUtc), (parsed.UserId, parsed.IssuedUtc));
        Assert.Throws<FormatException>(() => RecoveryChallenge.Parse(OwnerKeyCode.Encode(new byte[20])));
    }
}
