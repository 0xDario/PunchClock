using Microsoft.Data.Sqlite;
using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;
using PunchClock.Core.Employees;
using PunchClock.Core.Punches;

namespace PunchClock.Tests;

public sealed class EmployeeServiceTests : DatabaseTest
{
    [Fact]
    public async Task Stores_a_hash_never_the_pin()
    {
        var id = await Db.AddEmployeeAsync("482193");

        var stored = await Db.ScalarAsync<string>("SELECT pin_hash FROM employee WHERE id = $id;", ("$id", id));

        Assert.StartsWith("pbkdf2-sha256$", stored);
        Assert.DoesNotContain("482193", stored);
        Assert.True(TestDatabase.FastHasher.Verify("482193", stored));
    }

    [Theory]
    [InlineData("Ada", "Lovelace", "12")]
    [InlineData("Ada", "Lovelace", "12ab")]
    [InlineData(" ", "Lovelace", "1234")]
    [InlineData("Ada", "", "1234")]
    public async Task Rejects_invalid_input(string first, string last, string pin)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Db.Employees.CreateAsync(AuditActor.System, first, last, pin));
        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT count(*) FROM employee;"));
    }

    [Fact]
    public async Task Employees_cannot_create_employees()
    {
        var id = await Db.AddEmployeeAsync();

        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            Db.Employees.CreateAsync(AuditActor.ForEmployee(id), "Self", "Promoted", "1234"));

        Assert.Contains("manager, admin or migration only", ex.Message);
    }

    [Fact]
    public async Task Lists_active_employees_by_last_name()
    {
        await Db.AddEmployeeAsync(first: "Grace", last: "Hopper");
        var retired = await Db.AddEmployeeAsync(first: "Alan", last: "Turing");
        await Db.AddEmployeeAsync(first: "Ada", last: "Lovelace");
        await Db.Employees.SetActiveAsync(AuditActor.System, retired, isActive: false);

        var names = (await Db.Employees.ListActiveAsync()).Select(e => e.DisplayName);

        Assert.Equal(["Grace Hopper", "Ada Lovelace"], names);
        Assert.Equal(3, (await Db.Employees.ListAsync(activeOnly: false)).Count);
    }

    [Fact]
    public async Task Changing_a_pin_requires_the_current_one()
    {
        var id = await Db.AddEmployeeAsync("1111");

        Assert.Equal(PinChangeResult.InvalidCurrentPin, await Db.Employees.ChangeOwnPinAsync(id, "2222", "3333"));
        Assert.Equal(PinChangeResult.NewPinRejected, await Db.Employees.ChangeOwnPinAsync(id, "1111", "33"));
        Assert.Equal(PinChangeResult.EmployeeNotFound, await Db.Employees.ChangeOwnPinAsync(999, "1111", "3333"));
        Assert.Equal(PinChangeResult.Changed, await Db.Employees.ChangeOwnPinAsync(id, "1111", "0333"));

        Assert.False((await Db.Punches.PunchAsync(id, "1111", PunchDirection.In)).Accepted);
        Assert.True((await Db.Punches.PunchAsync(id, "0333", PunchDirection.In)).Accepted);
    }

    [Fact]
    public async Task Forced_pin_change_blocks_punching_until_a_new_pin_is_chosen()
    {
        // As the importer leaves every legacy employee.
        var id = await Db.AddEmployeeAsync("1234");
        await Db.ExecuteAsync(AuditActor.System, "UPDATE employee SET pin_must_change = 1 WHERE id = $id;", ("$id", id));

        // Declining the change and trying again never lets the known legacy PIN punch.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var refused = await Db.Punches.PunchAsync(id, "1234", PunchDirection.In);
            Assert.Equal(PunchRejection.PinChangeRequired, refused.Rejection);
        }

        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT count(*) FROM punch WHERE employee_id = $id;", ("$id", id)));

        Assert.Equal(PinChangeResult.Changed, await Db.Employees.ChangeOwnPinAsync(id, "1234", "5678"));
        Assert.True((await Db.Punches.PunchAsync(id, "5678", PunchDirection.In)).Accepted);
        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT pin_must_change FROM employee WHERE id = $id;", ("$id", id)));
        Assert.Equal(1, await Db.ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE table_name = 'employee' AND action = 'UPDATE' AND actor_kind = 'employee' AND actor_id = $id;",
            ("$id", id)));
    }

    [Fact]
    public async Task Manager_resets_a_forgotten_pin_to_a_temporary_one_the_employee_must_replace()
    {
        var id = await Db.AddEmployeeAsync("1234");
        var manager = await Db.AddManagerAsync();

        Assert.Equal(PinResetResult.Reset, await Db.Employees.ResetPinAsync(manager, id, "9999", "Forgot PIN"));

        Assert.Equal(PunchRejection.InvalidPin, (await Db.Punches.PunchAsync(id, "1234", PunchDirection.In)).Rejection);
        Assert.Equal(PunchRejection.PinChangeRequired, (await Db.Punches.PunchAsync(id, "9999", PunchDirection.In)).Rejection);
        Assert.Equal(PinChangeResult.NewPinRejected, await Db.Employees.ChangeOwnPinAsync(id, "9999", "9999"));
        Assert.Equal(PinChangeResult.Changed, await Db.Employees.ChangeOwnPinAsync(id, "9999", "2468"));
        Assert.True((await Db.Punches.PunchAsync(id, "2468", PunchDirection.In)).Accepted);

        Assert.Equal("Forgot PIN", await Db.ScalarAsync<string>("""
            SELECT reason FROM audit_log
             WHERE table_name = 'employee' AND row_id = $id AND action = 'UPDATE' AND actor_kind = 'user' AND actor_id = $by;
            """, ("$id", id), ("$by", manager.Id)));
        Assert.Empty(await Db.VerifyAsync());
    }

    [Fact]
    public async Task Pin_reset_needs_a_manager_or_admin_a_reason_and_a_valid_pin()
    {
        var id = await Db.AddEmployeeAsync("1234");
        var manager = await Db.AddManagerAsync();
        var migration = manager with { Id = AuditActor.Migration.Id, Role = UserRole.Migration };

        Assert.Equal(PinResetResult.NotAllowed, await Db.Employees.ResetPinAsync(migration, id, "9999", "Forgot PIN"));
        Assert.Equal(PinResetResult.NotAllowed, await Db.Employees.ResetPinAsync(manager, id, "9999", " "));
        Assert.Equal(PinResetResult.PinRejected, await Db.Employees.ResetPinAsync(manager, id, "12", "Forgot PIN"));
        Assert.Equal(PinResetResult.EmployeeNotFound, await Db.Employees.ResetPinAsync(manager, 999, "9999", "Forgot PIN"));
        Assert.True((await Db.Punches.PunchAsync(id, "1234", PunchDirection.In)).Accepted);
    }
}
