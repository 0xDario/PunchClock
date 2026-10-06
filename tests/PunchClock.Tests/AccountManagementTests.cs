using Microsoft.Data.Sqlite;
using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;
using PunchClock.Core.Punches;

namespace PunchClock.Tests;

public sealed class AccountManagementTests : DatabaseTest
{
    private const string Temporary = "temporary pass 1";

    [Fact]
    public async Task Admin_creates_a_named_manager_who_must_choose_their_own_password()
    {
        var admin = await Db.AddAdminAsync();

        Assert.Equal(AccountChangeResult.Changed,
            await Db.Accounts.CreateAccountAsync(admin, " jsmith ", "Jo Smith", UserRole.Manager, Temporary, employeeId: null));

        var manager = (await Db.Accounts.SignInAsync("jsmith", Temporary))!;
        Assert.Equal("Jo Smith", manager.DisplayName);
        Assert.True(manager.MustChangePassword);
        Assert.Equal(1, await Db.ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE table_name = 'app_user' AND action = 'INSERT' AND actor_kind = 'user' AND actor_id = $by AND row_id = $id;",
            ("$by", admin.Id), ("$id", manager.Id)));

        Assert.Equal(PasswordChangeResult.PasswordRejected, await Db.Accounts.ChangeOwnPasswordAsync(manager, Temporary, Temporary));
        Assert.Equal(PasswordChangeResult.PasswordRejected, await Db.Accounts.ChangeOwnPasswordAsync(manager, Temporary, "short"));
        Assert.Equal(PasswordChangeResult.Changed, await Db.Accounts.ChangeOwnPasswordAsync(manager, Temporary, "my own passphrase"));

        Assert.Null(await Db.Accounts.SignInAsync("jsmith", Temporary));
        Assert.False((await Db.Accounts.SignInAsync("jsmith", "my own passphrase"))!.MustChangePassword);
        Assert.Empty(await Db.VerifyAsync());
    }

    [Fact]
    public async Task Account_creation_is_validated()
    {
        var admin = await Db.AddAdminAsync();
        var manager = await Db.AddManagerAsync();
        var employee = await Db.AddEmployeeAsync();
        await Db.Accounts.CreateAccountAsync(admin, "linked", "Linked", UserRole.Manager, Temporary, employee);

        Assert.Equal(AccountChangeResult.NotAllowed,
            await Db.Accounts.CreateAccountAsync(manager, "x", "X", UserRole.Manager, Temporary, null));
        Assert.Equal(AccountChangeResult.NotAllowed,
            await Db.Accounts.CreateAccountAsync(admin, "x", "X", UserRole.Migration, Temporary, null));
        Assert.Equal(AccountChangeResult.NotAllowed,
            await Db.Accounts.CreateAccountAsync(admin, " ", "X", UserRole.Manager, Temporary, null));
        Assert.Equal(AccountChangeResult.PasswordRejected,
            await Db.Accounts.CreateAccountAsync(admin, "x", "X", UserRole.Manager, "short", null));
        Assert.Equal(AccountChangeResult.UsernameTaken,
            await Db.Accounts.CreateAccountAsync(admin, "MANAGER", "X", UserRole.Manager, Temporary, null));
        Assert.Equal(AccountChangeResult.EmployeeNotFound,
            await Db.Accounts.CreateAccountAsync(admin, "x", "X", UserRole.Manager, Temporary, 999));
        Assert.Equal(AccountChangeResult.EmployeeAlreadyLinked,
            await Db.Accounts.CreateAccountAsync(admin, "x", "X", UserRole.Manager, Temporary, employee));
        Assert.Null(await Db.Accounts.SignInAsync("x", Temporary));
    }

    [Fact]
    public async Task Managers_cannot_create_accounts_even_past_the_app()
    {
        var manager = await Db.AddManagerAsync();

        var ex = await Assert.ThrowsAsync<SqliteException>(() => Db.ExecuteAsync(AuditActor.ForUser(manager.Id), """
            INSERT INTO app_user (username, display_name, role, password_hash) VALUES ('sneaky', 'Sneaky', 'admin', 'x');
            """));

        Assert.Contains("not authorized", ex.Message);
    }

    [Fact]
    public async Task A_manager_linked_to_their_employee_record_cannot_correct_their_own_punches()
    {
        var admin = await Db.AddAdminAsync();
        var employee = await Db.AddEmployeeAsync();
        await Db.Accounts.CreateAccountAsync(admin, "lead", "Shift Lead", UserRole.Manager, Temporary, employee);
        var lead = (await Db.Accounts.SignInAsync("lead", Temporary))!;
        var punch = (await Db.Punches.PunchAsync(employee, "1234", PunchDirection.In)).Punch!;

        Assert.Equal(employee, lead.EmployeeId);
        Assert.Equal(CorrectionResult.OwnPunches, await Db.Corrections.VoidAsync(lead, punch.Id, "Did not really work today"));
        var ex = await Assert.ThrowsAsync<SqliteException>(() => Db.CorrectAsync(new NewCorrection(
            CorrectionAction.Void, employee, punch.Id, null, null, null, "Did not really work today", lead.Id)));
        Assert.Contains("own punches", ex.Message);

        // Another manager can.
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.VoidAsync(admin, punch.Id, "Punched in by mistake"));
    }

    [Fact]
    public async Task Admin_links_and_unlinks_an_account_but_never_their_own()
    {
        var admin = await Db.AddAdminAsync();
        var manager = await Db.AddManagerAsync();
        var employee = await Db.AddEmployeeAsync();

        Assert.Equal(AccountChangeResult.ReasonRequired, await Db.Accounts.SetEmployeeLinkAsync(admin, manager.Id, employee, " "));
        Assert.Equal(AccountChangeResult.Changed, await Db.Accounts.SetEmployeeLinkAsync(admin, manager.Id, employee, "Also works shifts"));
        Assert.Equal(employee, (await Db.Accounts.ListAsync()).Single(u => u.Id == manager.Id).EmployeeId);
        var otherAdmin = (await Db.Accounts.ListAsync()).Single(u => u.Username == "admin-for-manager");
        Assert.Equal(AccountChangeResult.EmployeeAlreadyLinked,
            await Db.Accounts.SetEmployeeLinkAsync(admin, otherAdmin.Id, employee, "Wrong account"));
        Assert.Equal(AccountChangeResult.NotAllowed,
            await Db.Accounts.SetEmployeeLinkAsync(admin, AuditActor.Migration.Id, null, "Service account"));
        Assert.Equal(AccountChangeResult.Changed, await Db.Accounts.SetEmployeeLinkAsync(admin, manager.Id, null, "No longer on shifts"));
        Assert.Null((await Db.Accounts.ListAsync()).Single(u => u.Id == manager.Id).EmployeeId);

        Assert.Equal(AccountChangeResult.NotAllowed, await Db.Accounts.SetEmployeeLinkAsync(admin, admin.Id, employee, "Linking myself"));
        Assert.Equal(AccountChangeResult.NotAllowed, await Db.Accounts.SetEmployeeLinkAsync(manager, admin.Id, employee, "Not an admin"));
        var ex = await Assert.ThrowsAsync<SqliteException>(() => Db.ExecuteAsync(AuditActor.ForUser(admin.Id),
            "UPDATE app_user SET employee_id = $e WHERE id = $id;", ("$e", employee), ("$id", admin.Id)));
        Assert.Contains("own employee link", ex.Message);
    }

    [Fact]
    public async Task Admin_resets_a_forgotten_password_which_clears_the_lockout_and_forces_a_change()
    {
        var admin = await Db.AddAdminAsync();
        var manager = await Db.AddManagerAsync();
        for (var i = 0; i < AccountService.MaxFailedSignIns; i++)
        {
            await Db.Accounts.SignInAsync("manager", $"guess number {i}");
        }

        Assert.Equal(AccountChangeResult.ReasonRequired, await Db.Accounts.ResetPasswordAsync(admin, manager.Id, Temporary, ""));
        Assert.Equal(AccountChangeResult.PasswordRejected, await Db.Accounts.ResetPasswordAsync(admin, manager.Id, "short", "Forgot password"));
        Assert.Equal(AccountChangeResult.NotAllowed, await Db.Accounts.ResetPasswordAsync(admin, admin.Id, Temporary, "Forgot password"));
        Assert.Equal(AccountChangeResult.NotAllowed, await Db.Accounts.ResetPasswordAsync(admin, AuditActor.Migration.Id, Temporary, "Forgot password"));
        Assert.Equal(AccountChangeResult.NotAllowed, await Db.Accounts.ResetPasswordAsync(manager, admin.Id, Temporary, "Forgot password"));
        Assert.Equal(AccountChangeResult.Changed, await Db.Accounts.ResetPasswordAsync(admin, manager.Id, Temporary, "Forgot password"));

        Assert.True((await Db.Accounts.SignInAsync("manager", Temporary))!.MustChangePassword);
        Assert.Equal("Forgot password", await Db.ScalarAsync<string>(
            "SELECT reason FROM audit_log WHERE table_name = 'app_user' AND row_id = $id ORDER BY seq DESC LIMIT 1;", ("$id", manager.Id)));
        Assert.Empty(await Db.VerifyAsync());
    }

    [Fact]
    public async Task Wrong_current_passwords_count_toward_the_sign_in_lockout()
    {
        var admin = await Db.AddAdminAsync();
        for (var i = 0; i < AccountService.MaxFailedSignIns; i++)
        {
            Assert.Equal(PasswordChangeResult.InvalidCurrentPassword,
                await Db.Accounts.ChangeOwnPasswordAsync(admin, $"guess number {i}", "a new passphrase"));
        }

        Assert.Equal(PasswordChangeResult.TooManyAttempts,
            await Db.Accounts.ChangeOwnPasswordAsync(admin, "correct horse battery", "a new passphrase"));
        await Assert.ThrowsAsync<SignInLockedException>(() => Db.Accounts.SignInAsync(admin.Username, "correct horse battery"));
        Assert.Empty(await Db.VerifyAsync());
    }
}
