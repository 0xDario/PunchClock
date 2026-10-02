using Microsoft.Data.Sqlite;
using PunchClock.Core.Audit;
using PunchClock.Core.Punches;
using PunchClock.Data.Sqlite;

namespace PunchClock.Tests;

public sealed class AuditTests : DatabaseTest
{
    [Fact]
    public async Task Writes_without_an_actor_are_refused()
    {
        var ex = await Assert.ThrowsAsync<SqliteException>(() => Db.ExecuteAsync(null,
            "INSERT INTO employee (first_name, last_name, pin_hash) VALUES ('No', 'Actor', 'x');"));

        Assert.Contains("no actor", ex.Message);
        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT count(*) FROM employee;"));
    }

    [Fact]
    public async Task Generic_sqlite_tools_cannot_write()
    {
        // A plain connection, like DB Browser or the sqlite3 CLI: no pc_sha256, no pc_ctx.
        var id = await Db.AddEmployeeAsync();
        await using var connection = new SqliteConnection($"Data Source={Db.Database.Path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE employee SET is_active = 0 WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        var ex = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());

        Assert.Contains("no such function", ex.Message);
    }

    [Fact]
    public async Task Every_write_is_attributed_and_the_chain_verifies()
    {
        var admin = await Db.AddAdminAsync();
        var employee = await Db.AddEmployeeAsync();
        await Db.Punches.PunchAsync(employee, "1234", PunchDirection.In);
        await Db.Punches.PunchAsync(employee, "9999", PunchDirection.Out);
        await Db.Site.SetTimeZoneAsync(admin, TimeZoneInfo.Utc.Id);

        var actions = await Db.ColumnAsync("""
            SELECT action || ':' || COALESCE(table_name, '-') || ':' || actor_kind || ':' || actor_id || ':' || client
            FROM audit_log WHERE seq > 1 ORDER BY seq;
            """);

        Assert.Contains($"INSERT:punch:employee:{employee}:TEST-PC/0.0.0", actions);
        Assert.Contains($"AUTH_PIN_FAILED:-:employee:{employee}:TEST-PC/0.0.0", actions);
        Assert.Contains($"UPDATE:site_setting:user:{admin.Id}:TEST-PC/0.0.0", actions);
        Assert.Empty(await Db.VerifyAsync());
    }

    [Fact]
    public async Task Pin_hashes_never_enter_the_audit_log()
    {
        var id = await Db.AddEmployeeAsync("482193");
        var hash = await Db.ScalarAsync<string>("SELECT pin_hash FROM employee WHERE id = $id;", ("$id", id));

        var leaked = await Db.ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE instr(COALESCE(after_json, '') || COALESCE(before_json, ''), $hash) > 0;",
            ("$hash", hash));

        Assert.Equal(0, leaked);
    }

    [Fact]
    public async Task A_correction_supersedes_the_punch_and_keeps_before_and_after()
    {
        var manager = await Db.AddManagerAsync();
        var employee = await Db.AddEmployeeAsync();
        var punchIn = (await Db.Punches.PunchAsync(employee, "1234", PunchDirection.In)).Punch!;

        var correctedTime = SqliteTime.Truncate(punchIn.OccurredAtUtc.AddMinutes(-30));
        await Db.CorrectAsync(new NewCorrection(CorrectionAction.Adjust, employee, punchIn.Id, PunchDirection.In,
            correctedTime, 0, "Badge reader was down, supervisor confirmed", manager.Id));

        await using (var uow = await Db.Store.BeginAsync())
        {
            var latest = (await uow.FindLatestPunchAsync(employee))!;
            Assert.Equal(PunchSource.Correction, latest.Source);
            Assert.Equal(correctedTime, latest.OccurredAtUtc);
        }

        var before = await Db.ScalarAsync<string>("SELECT before_json FROM audit_log WHERE table_name = 'punch_correction';");
        Assert.Contains($"\"id\":{punchIn.Id}", before);
        Assert.Empty(await Db.VerifyAsync());
    }

    [Fact]
    public async Task Managers_cannot_correct_their_own_punches()
    {
        var manager = await Db.AddManagerAsync();
        var employee = await Db.AddEmployeeAsync();
        var admin = (await Db.Accounts.ListAsync()).First(u => u.Role == Core.Accounts.UserRole.Admin);
        await Db.ExecuteAsync(AuditActor.ForUser(admin.Id), "UPDATE app_user SET employee_id = $e WHERE id = $m;",
            ("$e", employee), ("$m", manager.Id));

        var ex = await Assert.ThrowsAsync<SqliteException>(() => Db.CorrectAsync(new NewCorrection(
            CorrectionAction.Add, employee, null, PunchDirection.In, SqliteTime.Truncate(DateTimeOffset.UtcNow), 0,
            "Adding my own missing punch", manager.Id)));

        Assert.Contains("own punches", ex.Message);
    }
}
