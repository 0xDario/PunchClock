using Microsoft.Data.Sqlite;
using PunchClock.Core.Security;
using PunchClock.Migration.Analysis;
using PunchClock.Data.Sqlite;
using PunchClock.Migration.Database;
using PunchClock.Migration.Export;

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
