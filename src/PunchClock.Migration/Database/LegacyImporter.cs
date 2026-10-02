using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PunchClock.Core.Audit;
using PunchClock.Core.Security;
using PunchClock.Data.Sqlite;
using PunchClock.Migration.Analysis;
using PunchClock.Migration.Reporting;

namespace PunchClock.Migration.Database;

public sealed class ImportRefusedException(string message) : Exception(message);

/// <summary>
/// Writes an <see cref="ImportPlan"/> into a database that carries the audit
/// schema (docs/database/schema.sql, shipped as migration 0001_audit_schema.sql), following its section 9: one transaction
/// as the <c>migration</c> account, raw legacy rows kept as evidence, every
/// anomaly in <c>migration_issue</c>, and the batch closed only when the
/// database reconciles with the plan. Any mismatch rolls everything back.
/// </summary>
public sealed class LegacyImporter
{
    readonly IPinHasher _hasher;
    readonly string _toolVersion;

    public LegacyImporter(IPinHasher hasher, string toolVersion)
    {
        _hasher = hasher;
        _toolVersion = toolVersion;
    }

    /// <param name="database">Already migrated, e.g. by <see cref="PunchClockDatabase.OpenAndMigrateAsync"/>.</param>
    public async Task<ImportOutcome> ImportAsync(ImportPlan plan, SqliteDatabase database, CancellationToken ct = default)
    {
        var context = new AuditContext(database.Client, AuditActor.Migration, "Legacy Access import");
        await using var connection = await database.OpenAsync(context, ct);
        await using var tx = connection.BeginTransaction(deferred: false);

        await CheckTargetAsync(connection, tx, ct);

        var m = plan.Export.Manifest;
        var batchId = await InsertAsync(connection, tx, """
            INSERT INTO import_batch (source_file_name, source_sha256, manifest_sha256, source_time_zone_id,
                                      tool_version, manifest_employee_rows, manifest_shift_rows)
            VALUES ($file, $src, $manifest, $tz, $tool, $emp, $shift) RETURNING id
            """, ct,
            ("$file", Path.GetFileName((m.SourcePath ?? "PunchClock.accdb").Replace('\\', '/'))),
            ("$src", m.SourceSha256),
            ("$manifest", m.ManifestSha256),
            ("$tz", plan.TimeZone.Id),
            ("$tool", $"{_toolVersion}; export {m.Tool}"),
            ("$emp", Table(plan, "Employee").RowCount),
            ("$shift", Table(plan, "Shift").RowCount));

        // Evidence first: every exported row, verbatim except the PIN.
        foreach (var e in plan.Export.Employees)
        {
            await InsertAsync(connection, tx, """
                INSERT INTO legacy_employee_raw (import_batch_id, legacy_employee_id, first_name, last_name, is_active, pin_digits)
                VALUES ($batch, $id, $first, $last, $active, $digits)
                """, ct,
                ("$batch", batchId), ("$id", e.EmployeeId), ("$first", e.FirstName), ("$last", e.LastName),
                ("$active", e.IsActiveRaw), ("$digits", e.PinCode?.Length));
        }

        foreach (var s in plan.Export.Shifts)
        {
            await InsertAsync(connection, tx, """
                INSERT INTO legacy_shift_raw (import_batch_id, legacy_shift_id, legacy_employee_id,
                                              time_in_local, time_in_oadate, time_out_local, time_out_oadate)
                VALUES ($batch, $id, $emp, $in, $inOa, $out, $outOa)
                """, ct,
                ("$batch", batchId), ("$id", s.ShiftId), ("$emp", s.EmployeeId),
                ("$in", s.Raw.TimeIn), ("$inOa", s.Raw.TimeInOADate), ("$out", s.Raw.TimeOut), ("$outOa", s.Raw.TimeOutOADate));
        }

        // Employees. Every legacy PIN must be treated as known: it was stored in
        // plain text under a password published in the README.
        var employeeIds = new Dictionary<long, long>();
        foreach (var e in plan.Employees)
        {
            var pinHash = _hasher.Hash(e.LegacyPin ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            employeeIds[e.LegacyEmployeeId] = await InsertAsync(connection, tx, """
                INSERT INTO employee (legacy_id, first_name, last_name, pin_hash, pin_must_change, is_active)
                VALUES ($legacy, $first, $last, $pin, 1, $active) RETURNING id
                """, ct,
                ("$legacy", e.LegacyEmployeeId), ("$first", e.FirstName), ("$last", e.LastName),
                ("$pin", pinHash), ("$active", e.IsActive ? 1 : 0));

            await IssueAsync(connection, tx, batchId, "Employee", e.LegacyEmployeeId, "PIN_RESET_REQUIRED", Disposition.Imported,
                e.LegacyPin is null
                    ? new Dictionary<string, object?> { ["reason"] = "no legacy PIN; set one before this employee can punch" }
                    : new Dictionary<string, object?> { ["pin_digits"] = e.LegacyPin.Length },
                ct);
        }

        // Anomalies, in the schema's vocabulary. Report-only findings stay in the report files.
        foreach (var f in plan.Findings)
        {
            if (FindingInfo.SchemaCode(f.Code) is not { } code || f.LegacyId is not { } pk)
                continue;
            var disposition = f.Table == "Shift"
                ? plan.Shifts.Single(s => s.Source.ShiftId == pk).Disposition
                : Disposition.ImportedFlagged;
            if (disposition == Disposition.Imported)
                disposition = Disposition.ImportedFlagged;
            await IssueAsync(connection, tx, batchId, f.Table, pk, code, disposition,
                new Dictionary<string, object?> { ["finding"] = f.Code.ToString(), ["detail"] = f.Message }, ct);
        }

        // Punches: each imported shift becomes an IN and, when closed, an OUT.
        var punches = 0;
        foreach (var s in plan.Shifts.Where(s => s.Disposition != Disposition.Skipped).OrderBy(s => s.Source.ShiftId))
        {
            var employeeId = employeeIds[s.Employee!.LegacyEmployeeId];
            await InsertPunchAsync(connection, tx, employeeId, "IN", s.In!.Value, batchId, s.Source.ShiftId, ct);
            punches++;
            if (s.Out is { } o)
            {
                await InsertPunchAsync(connection, tx, employeeId, "OUT", o, batchId, s.Source.ShiftId, ct);
                punches++;
            }
        }

        await VerifyAsync(connection, tx, plan, batchId, punches, ct);

        // The schema refuses this unless the raw tables hold exactly the manifest's counts.
        await ExecuteAsync(connection, tx,
            "UPDATE import_batch SET completed_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = $id", ct,
            ("$id", batchId));

        var (seq, hash) = await ChainHeadAsync(connection, tx, ct);
        var importedAt = DateTime.Parse(
            (string)(await ScalarAsync(connection, tx, "SELECT completed_utc FROM import_batch WHERE id = $id", ct, ("$id", batchId)))!,
            CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        await tx.CommitAsync(ct);
        return new ImportOutcome(database.Path, batchId, importedAt, punches, seq, hash);
    }

    static Export.ManifestTable Table(ImportPlan plan, string name) =>
        plan.Export.Manifest.Tables.Single(t => t.Name == name);

    /// <summary>The import is a one-time cutover into a fresh database.</summary>
    static async Task CheckTargetAsync(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        var hasSchema = (long)(await ScalarAsync(c, tx,
            "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name IN ('import_batch', 'legacy_shift_raw', 'audit_log')", ct))!;
        if (hasSchema != 3)
            throw new ImportRefusedException("This database does not have the audit schema the importer writes to. The importer and the app must come from the same release.");

        var migration = await ScalarAsync(c, tx,
            "SELECT is_active FROM app_user WHERE id = $id AND role = 'migration'", ct, ("$id", AuditActor.Migration.Id));
        if (migration is not 1L)
            throw new ImportRefusedException("The migration account is missing or deactivated, so this database has already been through its cutover.");

        var previous = await ScalarAsync(c, tx,
            "SELECT started_utc FROM import_batch WHERE completed_utc IS NOT NULL ORDER BY id LIMIT 1", ct);
        if (previous is string when)
            throw new ImportRefusedException($"Legacy data was already imported into this database on {when}. Importing twice would duplicate every shift.");

        var employees = (long)(await ScalarAsync(c, tx, "SELECT count(*) FROM employee", ct))!;
        var punches = (long)(await ScalarAsync(c, tx, "SELECT count(*) FROM punch", ct))!;
        if (employees > 0 || punches > 0)
            throw new ImportRefusedException(
                $"This database already has {employees} employee(s) and {punches} punch(es). Import into a fresh database: " +
                "rename punchclock.db (keep it), start the new app once to create a new one, then import.");
    }

    /// <summary>
    /// Re-reads what was written, inside the same transaction, and compares it
    /// with the plan. Anything off throws, and the whole import rolls back.
    /// </summary>
    static async Task VerifyAsync(SqliteConnection c, SqliteTransaction tx, ImportPlan plan, long batchId, int punches, CancellationToken ct)
    {
        async Task Expect(string what, string sql, object expected)
        {
            var actual = await ScalarAsync(c, tx, sql, ct, ("$batch", batchId));
            if (!Equals(actual, expected))
                throw new InvalidOperationException($"Import check failed, nothing was saved: {what} is {actual}, expected {expected}.");
        }

        await Expect("employee count", "SELECT count(*) FROM employee WHERE legacy_id IS NOT NULL", (long)plan.Employees.Count);
        await Expect("distinct legacy employee IDs", "SELECT count(DISTINCT legacy_employee_id) FROM legacy_employee_raw WHERE import_batch_id = $batch", (long)plan.Export.Employees.Count);
        await Expect("distinct legacy shift IDs", "SELECT count(DISTINCT legacy_shift_id) FROM legacy_shift_raw WHERE import_batch_id = $batch", (long)plan.Export.Shifts.Count);
        await Expect("punch count", "SELECT count(*) FROM punch WHERE import_batch_id = $batch", (long)punches);

        // Hours per legacy employee, recomputed from the stored punches paired by
        // legacy shift, must equal the plan to the millisecond.
        var expected = plan.Totals.ByEmployee.ToDictionary(e => e.LegacyEmployeeId, e => (long)Math.Round(e.Elapsed.TotalMilliseconds));
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT e.legacy_id,
                   COALESCE(SUM(CAST(round((julianday(o.occurred_utc) - julianday(i.occurred_utc)) * 86400000) AS INTEGER)), 0)
              FROM employee e
              LEFT JOIN punch i ON i.employee_id = e.id AND i.direction = 'IN' AND i.import_batch_id = $batch
              LEFT JOIN punch o ON o.import_batch_id = i.import_batch_id AND o.legacy_shift_id = i.legacy_shift_id AND o.direction = 'OUT'
             WHERE e.legacy_id IS NOT NULL
             GROUP BY e.legacy_id
            """;
        cmd.Parameters.AddWithValue("$batch", batchId);
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var legacy = r.GetInt64(0);
                var ms = r.GetInt64(1);
                if (!expected.TryGetValue(legacy, out var want) || want != ms)
                    throw new InvalidOperationException($"Import check failed, nothing was saved: employee {legacy} has {ms} ms of shifts in the database, expected {want}.");
            }
        }

        var broken = await ScalarAsync(c, tx, "SELECT count(*) FROM verify_chain_v", ct);
        if (broken is not 0L)
            throw new InvalidOperationException($"Import check failed, nothing was saved: the audit chain reports {broken} problem(s).");
    }

    static Task InsertPunchAsync(SqliteConnection c, SqliteTransaction tx, long employeeId, string direction, ResolvedTime t, long batchId, long legacyShiftId, CancellationToken ct) =>
        ExecuteAsync(c, tx, """
            INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source, import_batch_id, legacy_shift_id)
            VALUES ($emp, $dir, $utc, $offset, 'legacy_import', $batch, $shift)
            """, ct,
            ("$emp", employeeId), ("$dir", direction), ("$utc", SqliteTime.ToText(new DateTimeOffset(t.Utc))),
            ("$offset", t.UtcOffsetMinutes), ("$batch", batchId), ("$shift", legacyShiftId));

    static Task IssueAsync(SqliteConnection c, SqliteTransaction tx, long batchId, string table, long pk, string code, Disposition disposition, Dictionary<string, object?> detail, CancellationToken ct) =>
        ExecuteAsync(c, tx, """
            INSERT INTO migration_issue (import_batch_id, legacy_table, legacy_pk, code, disposition, detail_json)
            VALUES ($batch, $table, $pk, $code, $disposition, $detail)
            """, ct,
            ("$batch", batchId), ("$table", table), ("$pk", pk), ("$code", code),
            ("$disposition", disposition switch
            {
                Disposition.Imported => "IMPORTED",
                Disposition.ImportedFlagged => "IMPORTED_FLAGGED",
                _ => "SKIPPED",
            }),
            ("$detail", JsonSerializer.Serialize(detail)));

    static async Task<(long Seq, string Hash)> ChainHeadAsync(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT seq, row_hash FROM audit_log ORDER BY seq DESC LIMIT 1";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetInt64(0), r.GetString(1));
    }

    static async Task<long> InsertAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        var v = await ScalarAsync(c, tx, sql, ct, args);
        return v is long id ? id : 0;
    }

    static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = Command(c, tx, sql, args);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    static async Task<object?> ScalarAsync(SqliteConnection c, SqliteTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = Command(c, tx, sql, args);
        return await cmd.ExecuteScalarAsync(ct);
    }

    static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
