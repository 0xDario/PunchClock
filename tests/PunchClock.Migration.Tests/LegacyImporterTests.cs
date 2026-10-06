using Microsoft.Data.Sqlite;
using PunchClock.Core.Security;
using PunchClock.Migration.Analysis;
using PunchClock.Data.Sqlite;
using PunchClock.Migration.Database;
using PunchClock.Migration.Export;
using PunchClock.Migration.Reporting;

namespace PunchClock.Migration.Tests;

public sealed class LegacyImporterTests : IAsyncLifetime
{
    static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
    static readonly Pbkdf2PinHasher FastHasher = new(iterations: 1_000);

    readonly ExportBuilder _export = new();
    readonly AuditSchemaDatabase _db = new();

    public ValueTask InitializeAsync() => _db.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        _export.Dispose();
        await _db.DisposeAsync();
    }

    static LegacyImporter Importer() => new(FastHasher, "PunchClock.Import/test");

    ImportPlan Plan()
    {
        _export
            .Employee(14, "Ann", "Lee", "123123")
            .Employee(15, "Bo", "Kim", "42", "0")
            .Employee(16, "Cy", "Ortiz", null)
            .Shift(30, 14, "2025-01-06 08:00:00", "2025-01-06 08:00:00")   // dummy
            .Shift(31, 14, "2025-01-07 08:00:00", "2025-01-07 16:30:00")
            .Shift(32, 12, "2025-01-07 09:00:00", "2025-01-07 10:00:00")   // orphan
            .Shift(33, 15, "2025-11-02 01:30:00", "2025-11-02 09:00:00")   // ambiguous, crosses DST
            .Shift(34, 14, "2025-01-08 08:00:00", null);                   // open, current
        _export.Build();
        return ImportAnalyzer.Analyze(LegacyExport.Load(_export.Folder), Toronto);
    }

    [Fact]
    public async Task Imports_employees_punches_evidence_and_issues_in_one_audited_batch()
    {
        var plan = Plan();

        var outcome = await Importer().ImportAsync(plan, _db.Database);

        Assert.Equal(5, outcome.PunchesWritten);
        Assert.Equal(3L, await _db.ScalarAsync("SELECT count(*) FROM employee WHERE pin_must_change = 1 AND legacy_id IN (14, 15, 16)"));
        Assert.Equal(0L, await _db.ScalarAsync("SELECT is_active FROM employee WHERE legacy_id = 15"));
        Assert.Equal(5L, await _db.ScalarAsync("SELECT count(*) FROM legacy_shift_raw"));
        Assert.Equal(3L, await _db.ScalarAsync("SELECT count(*) FROM legacy_employee_raw"));
        Assert.Equal(2L, await _db.ScalarAsync("SELECT pin_digits FROM legacy_employee_raw WHERE legacy_employee_id = 15"));
        Assert.Equal("2025-01-07T16:30:00.000", await _db.ScalarAsync("SELECT time_out_local FROM legacy_shift_raw WHERE legacy_shift_id = 31"));
        Assert.Equal(0L, await _db.ScalarAsync("SELECT count(*) FROM punch WHERE legacy_shift_id IN (30, 32)"));
        Assert.NotNull(await _db.ScalarAsync("SELECT completed_utc FROM import_batch"));

        var issues = (await _db.RowsAsync("SELECT legacy_pk, code, disposition FROM migration_issue ORDER BY legacy_table, legacy_pk, code"))
            .Select(r => $"{r[0]} {r[1]} {r[2]}").ToList();
        Assert.Contains("14 PIN_RESET_REQUIRED IMPORTED", issues);
        Assert.Contains("30 DUMMY_SHIFT SKIPPED", issues);
        Assert.Contains("32 ORPHAN_EMPLOYEE SKIPPED", issues);
        Assert.Contains("33 DST_AMBIGUOUS IMPORTED_FLAGGED", issues);
        Assert.Contains("33 CROSSES_DST IMPORTED_FLAGGED", issues);
        Assert.Contains("34 OPEN_SHIFT IMPORTED_FLAGGED", issues);

        // The import's own audit event carries the source hash and the manifest counts.
        var batchEvent = (string)(await _db.ScalarAsync(
            "SELECT after_json FROM audit_log WHERE table_name = 'import_batch' AND action = 'INSERT'"))!;
        Assert.Contains(new string('a', 64), batchEvent);
        Assert.Contains("\"manifest_shift_rows\":5", batchEvent.Replace(" ", ""));
        Assert.Equal(2L, await _db.ScalarAsync("SELECT DISTINCT actor_id FROM audit_log WHERE table_name = 'punch'"));
        Assert.Equal(0L, await _db.ScalarAsync("SELECT count(*) FROM verify_chain_v"));
        Assert.Equal(outcome.ChainSeq, await _db.ScalarAsync("SELECT max(seq) FROM audit_log"));
    }

    [Fact]
    public async Task Imported_pins_still_verify_and_shifts_keep_their_real_duration()
    {
        await Importer().ImportAsync(Plan(), _db.Database);

        var hash = (string)(await _db.ScalarAsync("SELECT pin_hash FROM employee WHERE legacy_id = 14"))!;
        Assert.True(FastHasher.Verify("123123", hash));

        // 01:30 EDT to 09:00 EST is 8.5 hours elapsed, 7.5 on the wall clock.
        Assert.Equal(30600L, await _db.ScalarAsync(
            "SELECT s.duration_sec FROM shift_v s JOIN employee e ON e.id = s.employee_id WHERE e.legacy_id = 15"));
        Assert.Equal("open", await _db.ScalarAsync(
            "SELECT s.status FROM shift_v s JOIN punch p ON p.id = s.in_punch_id WHERE p.legacy_shift_id = 34"));
    }

    [Fact]
    public async Task An_employee_without_a_pin_gets_a_temporary_one_listed_only_in_the_export_side_report()
    {
        var plan = Plan();

        var outcome = await Importer().ImportAsync(plan, _db.Database);

        var pin = Assert.Single(outcome.TemporaryPins, p => p.Key == 16).Value;
        Assert.Matches("^[0-9]{6}$", pin);
        var hash = (string)(await _db.ScalarAsync("SELECT pin_hash FROM employee WHERE legacy_id = 16"))!;
        Assert.True(FastHasher.Verify(pin, hash));
        Assert.Contains($"PIN {pin}", ImportReport.Text(plan, "test", outcome, withTemporaryPins: true));
        Assert.DoesNotContain(pin, ImportReport.Text(plan, "test", outcome));
        Assert.Equal(0L, await _db.ScalarAsync($"SELECT count(*) FROM migration_issue WHERE detail_json LIKE '%{pin}%'"));
    }

    [Fact]
    public async Task A_time_in_the_spring_forward_gap_is_stored_an_hour_later_with_its_issue_recorded()
    {
        _export.Employee(20, "Di", "Moreau").Shift(40, 20, "2026-03-08 02:30:00", "2026-03-08 04:00:00");
        _export.Build();
        var plan = ImportAnalyzer.Analyze(LegacyExport.Load(_export.Folder), Toronto);

        await Importer().ImportAsync(plan, _db.Database);

        var rows = await _db.RowsAsync("SELECT direction, occurred_utc, utc_offset_minutes FROM punch WHERE legacy_shift_id = 40 ORDER BY direction");
        Assert.Equal(["IN", "2026-03-08T07:30:00.000Z", -240L], rows[0]);
        Assert.Equal(["OUT", "2026-03-08T08:00:00.000Z", -240L], rows[1]);
        Assert.Equal(1L, await _db.ScalarAsync("SELECT count(*) FROM migration_issue WHERE legacy_pk = 40 AND code = 'DST_INVALID'"));
        foreach (var view in await _db.RowsAsync("SELECT name FROM sqlite_master WHERE type = 'view' AND name LIKE 'verify%'"))
            Assert.Equal(0L, await _db.ScalarAsync($"SELECT count(*) FROM {view[0]}"));
    }

    [Fact]
    public async Task Every_raw_row_left_out_is_recorded_as_skipped_so_the_batch_can_close()
    {
        _export.Employee(21, "Ed", "Park")
            .Shift(50, 21, "2025-01-06 08:00:00", "2025-01-06 08:00:00")   // dummy
            .Shift(51, null, "2025-01-07 08:00:00", "2025-01-07 12:00:00") // no employee
            .Shift(52, 21, null, "2025-01-08 12:00:00")                    // no punch-in
            .Shift(53, 99, "2025-01-09 08:00:00", "2025-01-09 12:00:00")   // orphan
            .Shift(54, 21, "2025-01-10 08:00:00", "2025-01-10 12:00:00");
        _export.Build();
        var plan = ImportAnalyzer.Analyze(LegacyExport.Load(_export.Folder), Toronto);

        var outcome = await Importer().ImportAsync(plan, _db.Database);

        Assert.Equal(2, outcome.PunchesWritten);
        Assert.NotNull(await _db.ScalarAsync("SELECT completed_utc FROM import_batch"));
        Assert.Equal(4L, await _db.ScalarAsync("SELECT count(DISTINCT legacy_pk) FROM migration_issue WHERE legacy_table = 'Shift' AND disposition = 'SKIPPED'"));
    }

    [Fact]
    public async Task Refuses_to_import_twice()
    {
        var plan = Plan();
        await Importer().ImportAsync(plan, _db.Database);

        var ex = await Assert.ThrowsAsync<ImportRefusedException>(() => Importer().ImportAsync(plan, _db.Database));
        Assert.Contains("already imported", ex.Message);
    }

    [Fact]
    public async Task Refuses_a_database_without_the_audit_schema()
    {
        var path = _db.Path + ".plain";
        await using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE employee (id INTEGER PRIMARY KEY);";
            await cmd.ExecuteNonQueryAsync();
        }

        var ex = await Assert.ThrowsAsync<ImportRefusedException>(() => Importer().ImportAsync(Plan(), new SqliteDatabase(path)));
        Assert.Contains("audit schema", ex.Message);
    }

    [Fact]
    public async Task A_failed_import_leaves_the_database_untouched()
    {
        var plan = Plan();
        var before = await _db.ScalarAsync("SELECT max(seq) FROM audit_log");

        // A plan whose totals disagree with what gets written must roll back.
        var bad = new ImportPlan
        {
            Export = plan.Export,
            TimeZone = plan.TimeZone,
            Employees = plan.Employees,
            Shifts = plan.Shifts,
            Findings = plan.Findings,
            Totals = plan.Totals with { ByEmployee = plan.Totals.ByEmployee.Select(e => e with { Elapsed = e.Elapsed + TimeSpan.FromSeconds(1) }).ToList() },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Importer().ImportAsync(bad, _db.Database));

        Assert.Equal(before, await _db.ScalarAsync("SELECT max(seq) FROM audit_log"));
        Assert.Equal(0L, await _db.ScalarAsync("SELECT count(*) FROM import_batch"));
    }
}
