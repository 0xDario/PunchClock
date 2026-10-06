using System.Security.Cryptography;
using System.Text;
using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;
using PunchClock.Core.Punches;
using PunchClock.Core.Reports;
using PunchClock.Data.Sqlite;

namespace PunchClock.Tests;

public sealed class ReportTests : DatabaseTest
{
    private static readonly string Toronto = OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/Toronto";

    private static TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(Toronto);

    protected override string? SiteZone => Toronto;

    private AppUser _manager = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _manager = await Db.AddManagerAsync();
    }

    /// <summary>A punch at a site wall-clock time, entered as a manager's correction.</summary>
    private async Task<long> PunchAt(long employeeId, PunchDirection direction, DateTime local)
    {
        var utc = PunchCorrectionService.ToUtc(local, Zone)!.Value;
        var offset = (int)Zone.GetUtcOffset(utc).TotalMinutes;
        await Db.CorrectAsync(new NewCorrection(
            CorrectionAction.Add, employeeId, null, direction, utc, offset, "Entered from the paper log", _manager.Id));
        return await Db.ScalarAsync<long>("SELECT max(id) FROM punch;");
    }

    private async Task Shift(long employeeId, DateTime inLocal, DateTime? outLocal)
    {
        await PunchAt(employeeId, PunchDirection.In, inLocal);
        if (outLocal is { } o)
        {
            await PunchAt(employeeId, PunchDirection.Out, o);
        }
    }

    [Theory]
    [InlineData("08:00:30", "08:10:20", 10)]
    [InlineData("08:00:59.600", "08:01:30", 0)]
    [InlineData("07:59:59.600", "08:01:00", 1)]
    [InlineData("08:00:00", "08:00:59.400", 0)]
    [InlineData("08:00:00", "08:00:59.500", 1)]
    public void Minutes_follow_access_datediff(string from, string to, long expected)
    {
        var day = new DateTime(2026, 9, 1);
        Assert.Equal(expected, PayRules.AccessDateDiffMinutes(day + TimeSpan.Parse(from), day + TimeSpan.Parse(to)));
    }

    [Fact]
    public async Task Pay_report_counts_shifts_like_the_old_report_and_lists_the_rest()
    {
        var ada = await Db.AddEmployeeAsync(first: "Ada", last: "Lovelace");
        var grace = await Db.AddEmployeeAsync(first: "Grace", last: "Hopper");
        await Shift(ada, new(2026, 8, 31, 22, 0, 0), new(2026, 9, 1, 2, 0, 0));       // starts before: another period
        await Shift(ada, new(2026, 9, 1, 8, 0, 30), new(2026, 9, 1, 16, 10, 20));     // 490 minutes
        await Shift(ada, new(2026, 9, 30, 22, 0, 0), new(2026, 10, 1, 6, 0, 0));      // ends after: listed
        await Shift(grace, new(2026, 9, 15, 9, 0, 0), null);                          // no punch-out: listed

        var report = await Db.Reports.PayReportAsync(_manager, new(2026, 9, 1), new(2026, 9, 30));

        Assert.Equal(["Grace Hopper", "Ada Lovelace"], report.Lines.Select(l => l.Name));
        var line = report.Lines.Single(l => l.EmployeeId == ada);
        Assert.Equal((1, 490L, 8.17m, 1), (line.Shifts, line.Minutes, line.Hours, line.NotCounted));
        Assert.Equal(489.83m, Math.Round(line.ElapsedMinutes, 2));
        Assert.Equal((0, 0L, 1), (report.Lines[0].Shifts, report.Lines[0].Minutes, report.Lines[0].NotCounted));
        Assert.Equal(2, report.NotCounted.Count);
        Assert.Equal(490, report.TotalMinutes);
    }

    [Fact]
    public async Task Pay_report_uses_wall_clock_minutes_across_a_dst_change_and_shows_elapsed()
    {
        var ada = await Db.AddEmployeeAsync();
        // Clocks went back at 02:00 on 2025-11-02: 8 hours on the wall, 9 actually worked.
        await Shift(ada, new(2025, 11, 1, 22, 0, 0), new(2025, 11, 2, 6, 0, 0));

        var line = (await Db.Reports.PayReportAsync(_manager, new(2025, 11, 1), new(2025, 11, 2))).Lines.Single();

        Assert.Equal(480, line.Minutes);
        Assert.Equal(540m, line.ElapsedMinutes);
    }

    [Fact]
    public async Task Superseded_punches_do_not_pay_but_are_exported()
    {
        var ada = await Db.AddEmployeeAsync();
        await Shift(ada, new(2026, 9, 2, 9, 0, 0), new(2026, 9, 2, 17, 0, 0));
        var outId = await Db.ScalarAsync<long>("SELECT max(id) FROM punch;");
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.AdjustAsync(
            _manager, outId, PunchDirection.Out, new DateTime(2026, 9, 2, 13, 0, 0), "Left at 1 pm, not 5 pm"));

        var line = (await Db.Reports.PayReportAsync(_manager, new(2026, 9, 1), new(2026, 9, 30))).Lines.Single();
        Assert.Equal(240, line.Minutes);

        var path = Path.Combine(Db.Folder, "punches.csv");
        var result = await Db.Reports.ExportPunchesAsync(_manager, new(2026, 9, 1), new(2026, 9, 30), path);
        var lines = File.ReadAllLines(path);
        Assert.Equal(3, result.Rows);
        Assert.StartsWith("punch_id,employee_id,legacy_id,employee,direction,site_time", lines[0].TrimStart('﻿'));
        Assert.Contains(lines, l => l.Contains(",OUT,2026-09-02 17:00:00,-240,", StringComparison.Ordinal) && l.Contains(",correction,no,", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(",OUT,2026-09-02 13:00:00,-240,", StringComparison.Ordinal) && l.Contains(",correction,yes,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_export_is_audited_with_the_file_hash()
    {
        var ada = await Db.AddEmployeeAsync();
        await Shift(ada, new(2026, 9, 1, 8, 0, 0), new(2026, 9, 1, 16, 0, 0));
        var path = Path.Combine(Db.Folder, "pay.csv");

        var result = await Db.Reports.ExportPayReportAsync(_manager, new(2026, 9, 1), new(2026, 9, 30), path);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Sha256);
        Assert.Equal(
            "employee_id,legacy_id,employee,shifts,minutes,hours,elapsed_hours,shifts_not_counted\r\n"
            + $"{ada},,Ada Lovelace,1,480,8,8,0\r\n",
            Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
        Assert.Equal(result.Sha256, await Db.ScalarAsync<string>("""
            SELECT json_extract(after_json, '$.sha256') FROM audit_log
             WHERE action = 'REPORT_EXPORT' AND actor_kind = 'user' AND actor_id = $by
               AND json_extract(after_json, '$.kind') = 'pay_report';
            """, ("$by", _manager.Id)));
        Assert.False(File.Exists(path + ".partial"));
        Assert.Empty(await Db.VerifyAsync());
    }

    [Fact]
    public async Task Corrections_export_names_who_and_why_and_defuses_formulas()
    {
        var ada = await Db.AddEmployeeAsync();
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone).DateTime;
        Assert.Equal(CorrectionResult.Corrected, await Db.Corrections.AddAsync(
            _manager, ada, PunchDirection.In, now.AddHours(-2), "=HYPERLINK(\"x\") forgot to punch"));
        var today = DateOnly.FromDateTime(now);
        var path = Path.Combine(Db.Folder, "corrections.csv");

        var result = await Db.Reports.ExportCorrectionsAsync(_manager, today.AddDays(-1), today.AddDays(1), path);

        var text = File.ReadAllText(path);
        Assert.Equal(1, result.Rows);
        Assert.Contains(",add,", text, StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\") forgot to punch\",manager,Manager", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_admins_and_managers_run_reports()
    {
        var migration = _manager with { Id = AuditActor.Migration.Id, Role = UserRole.Migration };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Db.Reports.PayReportAsync(migration, new(2026, 9, 1), new(2026, 9, 30)));
        await Assert.ThrowsAsync<ArgumentException>(() => Db.Reports.PayReportAsync(_manager, new(2026, 9, 30), new(2026, 9, 1)));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("-1+1", "'-1+1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData(" padded", "\" padded\"")]
    public void Csv_fields_are_quoted_and_defused(string value, string expected) => Assert.Equal(expected, Csv.Field(value));

    [Fact]
    public void Csv_numbers_are_invariant_and_never_defused() => Assert.Equal("-1.5", Csv.Field(-1.5m));

    [Fact]
    public async Task Backup_is_a_verified_copy_recorded_in_the_live_log()
    {
        var admin = await Db.AddAdminAsync();
        await Shift(await Db.AddEmployeeAsync(), new(2026, 9, 1, 8, 0, 0), new(2026, 9, 1, 16, 0, 0));
        var headBefore = await Db.ScalarAsync<long>("SELECT max(seq) FROM audit_log;");
        var path = Path.Combine(Db.Folder, "backup.db");

        var result = await Db.Maintenance.BackupAsync(admin, path);

        Assert.Equal(headBefore, result.AuditHeadSeq);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), result.Sha256);
        Assert.Equal(result.Sha256, await Db.ScalarAsync<string>(
            "SELECT json_extract(after_json, '$.sha256') FROM audit_log WHERE action = 'BACKUP' AND actor_id = $by;", ("$by", admin.Id)));

        // The copy is a complete, intact database.
        var copy = new SqliteDatabase(path);
        Assert.Empty(await AuditVerifier.FindProblemsAsync(copy));
        Assert.Empty(await new SchemaMigrator(copy).MigrateAsync());

        await Assert.ThrowsAsync<IOException>(() => Db.Maintenance.BackupAsync(admin, path));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Db.Maintenance.BackupAsync(_manager, Path.Combine(Db.Folder, "b2.db")));
        Assert.Empty(await Db.VerifyAsync());
    }

    [Fact]
    public async Task Backup_of_a_tampered_database_is_refused_and_removed()
    {
        var admin = await Db.AddAdminAsync();
        await Db.ExecuteAsync(null, "DROP TRIGGER punch_bd;");
        var path = Path.Combine(Db.Folder, "backup.db");

        await Assert.ThrowsAsync<SchemaMismatchException>(() => Db.Maintenance.BackupAsync(admin, path));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_fresh_database_has_no_warnings() => Assert.Empty(await Db.Maintenance.FindWarningsAsync());
}
