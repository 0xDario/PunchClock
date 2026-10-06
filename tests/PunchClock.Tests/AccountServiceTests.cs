using Microsoft.Data.Sqlite;
using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;

namespace PunchClock.Tests;

public sealed class AccountServiceTests : DatabaseTest
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task First_admin_can_be_created_exactly_once()
    {
        Assert.True(await Db.Accounts.NeedsFirstAdminAsync());

        var admin = await Db.Accounts.CreateFirstAdminAsync("owner", "Owner", Password);

        Assert.Equal(UserRole.Admin, admin.Role);
        Assert.False(await Db.Accounts.NeedsFirstAdminAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Db.Accounts.CreateFirstAdminAsync("second", "Second", Password));
        Assert.Equal(1, await Db.ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE table_name = 'app_user' AND action = 'INSERT' AND actor_id = 1 AND row_id = $id;",
            ("$id", admin.Id)));
    }

    [Theory]
    [InlineData("", "Owner", Password)]
    [InlineData("owner", " ", Password)]
    [InlineData("owner", "Owner", "short")]
    public async Task First_admin_input_is_validated(string username, string displayName, string password)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Db.Accounts.CreateFirstAdminAsync(username, displayName, password));
        Assert.True(await Db.Accounts.NeedsFirstAdminAsync());
    }

    [Fact]
    public async Task Sign_in_succeeds_only_with_the_right_password_and_is_audited()
    {
        var admin = await Db.Accounts.CreateFirstAdminAsync("owner", "Owner", Password);

        Assert.Null(await Db.Accounts.SignInAsync("owner", "wrong password!"));
        Assert.Null(await Db.Accounts.SignInAsync("nobody", Password));
        Assert.Null(await Db.Accounts.SignInAsync("system", ""));
        Assert.Equal(admin.Id, (await Db.Accounts.SignInAsync("OWNER", Password))!.Id);

        Assert.Equal(3, await Db.ScalarAsync<long>("SELECT count(*) FROM audit_log WHERE action = 'AUTH_LOGIN_FAILED';"));
        Assert.Equal(2, await Db.ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE action = 'AUTH_LOGIN' AND actor_id = $id;", ("$id", admin.Id)));
    }

    [Fact]
    public async Task Admin_deactivates_the_migration_account()
    {
        var admin = await Db.AddAdminAsync();

        var result = await Db.Accounts.SetActiveAsync(admin, AuditActor.Migration.Id, isActive: false, "Legacy import signed off");

        Assert.Equal(AccountChangeResult.Changed, result);
        Assert.False((await Db.Accounts.ListAsync()).Single(u => u.Role == UserRole.Migration).IsActive);
        Assert.Equal("Legacy import signed off", await Db.ScalarAsync<string>(
            "SELECT reason FROM audit_log WHERE table_name = 'app_user' AND action = 'UPDATE' AND row_id = 2;"));

        // The importer can no longer write once its account is inactive.
        var ex = await Assert.ThrowsAsync<SqliteException>(() => Db.ExecuteAsync(AuditActor.Migration, """
            INSERT INTO import_batch (source_file_name, source_sha256, manifest_sha256, source_time_zone_id,
                                      tool_version, manifest_employee_rows, manifest_shift_rows)
            VALUES ('x.accdb', $h, $h, 'UTC', 'test', 0, 0);
            """, ("$h", new string('a', 64))));
        Assert.Contains("migration account only", ex.Message);
    }

    [Fact]
    public async Task System_and_own_account_cannot_be_deactivated()
    {
        var admin = await Db.AddAdminAsync();

        Assert.Equal(AccountChangeResult.NotAllowed, await Db.Accounts.SetActiveAsync(admin, AuditActor.System.Id, false, "test reason"));
        Assert.Equal(AccountChangeResult.NotAllowed, await Db.Accounts.SetActiveAsync(admin, admin.Id, false, "test reason"));
        Assert.Equal(AccountChangeResult.NotFound, await Db.Accounts.SetActiveAsync(admin, 999, false, "test reason"));
    }

    [Fact]
    public async Task Managers_cannot_change_accounts_even_past_the_app()
    {
        var manager = await Db.AddManagerAsync();

        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            Db.Accounts.SetActiveAsync(manager, AuditActor.Migration.Id, isActive: false, "trying anyway"));

        Assert.Contains("not authorized", ex.Message);
    }

    [Fact]
    public async Task Inactive_accounts_cannot_sign_in()
    {
        var admin = await Db.AddAdminAsync();
        var manager = await Db.AddManagerAsync();
        await Db.Accounts.SetActiveAsync(admin, manager.Id, isActive: false, "left the company");

        Assert.Null(await Db.Accounts.SignInAsync("manager", "manager password"));
    }

    [Fact]
    public async Task Repeated_failed_sign_ins_lock_the_username_until_an_admin_changes_the_account()
    {
        var admin = await Db.AddAdminAsync();
        var manager = await Db.AddManagerAsync();
        for (var i = 0; i < AccountService.MaxFailedSignIns; i++)
        {
            Assert.Null(await Db.Accounts.SignInAsync("Manager ", $"guess number {i}"));
        }

        // Locked: even the right password is not checked.
        await Assert.ThrowsAsync<SignInLockedException>(() => Db.Accounts.SignInAsync("manager", "manager password"));
        Assert.Equal(admin.Id, (await Db.Accounts.SignInAsync(admin.Username, "correct horse battery"))!.Id);

        // An admin touching the account (here: deactivate and reactivate) clears the lock.
        await Db.Accounts.SetActiveAsync(admin, manager.Id, isActive: false, "Locked out after guesses");
        await Db.Accounts.SetActiveAsync(admin, manager.Id, isActive: true, "Owner confirmed by phone");
        Assert.Equal(manager.Id, (await Db.Accounts.SignInAsync("manager", "manager password"))!.Id);
    }

    [Fact]
    public async Task Unknown_usernames_lock_too_so_lockout_does_not_reveal_which_exist()
    {
        for (var i = 0; i < AccountService.MaxFailedSignIns; i++)
        {
            Assert.Null(await Db.Accounts.SignInAsync("ghost", "whatever password"));
        }

        await Assert.ThrowsAsync<SignInLockedException>(() => Db.Accounts.SignInAsync("GHOST", "whatever password"));
    }

    [Fact]
    public async Task Failed_sign_ins_keep_known_usernames_but_never_what_was_typed_for_unknown_ones()
    {
        await Db.AddAdminAsync();

        Assert.Null(await Db.Accounts.SignInAsync("Owner", "wrong password!"));
        Assert.Null(await Db.Accounts.SignInAsync("hunter2 my real password", ""));

        var details = await Db.ScalarAsync<string>(
            "SELECT json_group_array(json(after_json)) FROM audit_log WHERE action = 'AUTH_LOGIN_FAILED';");
        Assert.Contains("\"username\":\"owner\"", details);
        Assert.DoesNotContain("hunter2", details);
        Assert.Contains(AccountService.UsernameDigest("HUNTER2 my real password "), details);
    }

    [Fact]
    public async Task Every_failed_sign_in_costs_one_password_check_so_timing_reveals_no_usernames()
    {
        var admin = await Db.AddAdminAsync();
        await Db.Accounts.SetActiveAsync(admin, (await Db.AddManagerAsync()).Id, isActive: false, "Left the company");
        var hasher = new CountingHasher();
        var accounts = new AccountService(Db.Store, hasher);

        foreach (var username in new[] { "owner", "nobody", "manager", "system" })
        {
            hasher.Verifications = 0;
            Assert.Null(await accounts.SignInAsync(username, "wrong password!"));
            Assert.Equal(1, hasher.Verifications);
        }
    }

    private sealed class CountingHasher : PunchClock.Core.Security.IPinHasher
    {
        public int Verifications { get; set; }

        public string Hash(string pin) => TestDatabase.FastHasher.Hash(pin);

        public bool Verify(string pin, string encodedHash)
        {
            Verifications++;
            return TestDatabase.FastHasher.Verify(pin, encodedHash);
        }
    }
}
