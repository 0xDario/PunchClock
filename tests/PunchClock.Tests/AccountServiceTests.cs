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
}
