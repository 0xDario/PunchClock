using Microsoft.Data.Sqlite;
using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;
using PunchClock.Core.Employees;
using PunchClock.Core.Persistence;
using PunchClock.Core.Punches;
using PunchClock.Core.Reports;

namespace PunchClock.Data.Sqlite;

public sealed class SqlitePunchClockStore(SqliteDatabase database) : IPunchClockStore
{
    public async Task<IPunchClockUnitOfWork> BeginAsync(CancellationToken ct = default)
    {
        var context = database.CreateContext();
        var connection = await database.OpenAsync(context, ct);
        try
        {
            // BEGIN IMMEDIATE: take the write lock up front so a read-check-insert sequence
            // cannot interleave with another writer. Contention waits up to DefaultTimeout.
            var transaction = connection.BeginTransaction(deferred: false);

            // Startup checked the schema once; a kiosk runs for months, so every unit of work
            // checks again under the write lock before trusting the triggers to guard its writes.
            await SchemaMigrator.EnsureSchemaIntactAsync(connection, transaction, ct);
            return new SqliteUnitOfWork(connection, transaction, context);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal sealed class SqliteUnitOfWork(SqliteConnection connection, SqliteTransaction transaction, AuditContext context)
    : IPunchClockUnitOfWork
{
    private const string EmployeeColumns = "id, first_name, last_name, is_active, pin_hash, pin_must_change, legacy_id";
    private const string PunchColumns = "id, employee_id, direction, occurred_utc, utc_offset_minutes, recorded_utc, source";
    private const string UserColumns = "id, username, display_name, role, is_active, password_hash, employee_id, must_change_password";

    private bool _completed;

    public void ActAs(AuditActor actor, string? reason = null)
    {
        context.Actor = actor;
        context.Reason = reason;
    }

    public async Task RecordEventAsync(AuditEvent auditEvent, string? detailJson = null, CancellationToken ct = default)
    {
        await using var command = Command("""
            INSERT INTO audit_log (actor_kind, actor_id, client, action, after_json, reason)
            VALUES (pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), $action, $detail, pc_ctx('reason'));
            """);
        command.Parameters.AddWithValue("$action", auditEvent switch
        {
            AuditEvent.AuthLogin => "AUTH_LOGIN",
            AuditEvent.AuthLoginFailed => "AUTH_LOGIN_FAILED",
            AuditEvent.AuthLogout => "AUTH_LOGOUT",
            AuditEvent.AuthPinFailed => "AUTH_PIN_FAILED",
            AuditEvent.AppStart => "APP_START",
            AuditEvent.ReportExport => "REPORT_EXPORT",
            AuditEvent.Backup => "BACKUP",
            _ => throw new ArgumentOutOfRangeException(nameof(auditEvent), auditEvent, null),
        });
        command.Parameters.AddWithValue("$detail", (object?)detailJson ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        await using var command = Command("SELECT value FROM site_setting WHERE key = $key;");
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(ct);
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        await using var command = Command("""
            INSERT INTO site_setting (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value WHERE value IS NOT excluded.value;
            """);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<Employee?> FindEmployeeAsync(long employeeId, CancellationToken ct = default)
    {
        await using var command = Command($"SELECT {EmployeeColumns} FROM employee WHERE id = $id;");
        command.Parameters.AddWithValue("$id", employeeId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapEmployee(reader) : null;
    }

    public async Task<IReadOnlyList<Employee>> ListEmployeesAsync(bool activeOnly, CancellationToken ct = default)
    {
        await using var command = Command($"""
            SELECT {EmployeeColumns} FROM employee
            WHERE $all = 1 OR is_active = 1
            ORDER BY last_name COLLATE NOCASE, first_name COLLATE NOCASE, id;
            """);
        command.Parameters.AddWithValue("$all", activeOnly ? 0 : 1);
        return await ReadAllAsync(command, MapEmployee, ct);
    }

    public async Task<long> AddEmployeeAsync(NewEmployee employee, CancellationToken ct = default)
    {
        await using var command = Command("""
            INSERT INTO employee (first_name, last_name, pin_hash, pin_must_change)
            VALUES ($first, $last, $pin, $must)
            RETURNING id;
            """);
        command.Parameters.AddWithValue("$first", employee.FirstName);
        command.Parameters.AddWithValue("$last", employee.LastName);
        command.Parameters.AddWithValue("$pin", employee.PinHash);
        command.Parameters.AddWithValue("$must", employee.PinMustChange ? 1 : 0);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<int> CountRecentPinFailuresAsync(long employeeId, TimeSpan window, CancellationToken ct = default)
    {
        // Failures inside the window that nothing has cleared since: a later action by the
        // employee (a punch, a PIN change) or a change to their row (a manager's PIN reset).
        // The window runs on the database clock, like every audit timestamp.
        await using var command = Command("""
            SELECT count(*) FROM audit_log f
             WHERE f.action = 'AUTH_PIN_FAILED' AND f.actor_kind = 'employee' AND f.actor_id = $id
               AND f.occurred_utc > strftime('%Y-%m-%dT%H:%M:%fZ', 'now', $window)
               AND NOT EXISTS (
                   SELECT 1 FROM audit_log s
                    WHERE s.seq > f.seq
                      AND ((s.actor_kind = 'employee' AND s.actor_id = $id AND s.action <> 'AUTH_PIN_FAILED')
                           OR (s.table_name = 'employee' AND s.row_id = $id)));
            """);
        command.Parameters.AddWithValue("$id", employeeId);
        command.Parameters.AddWithValue("$window", FormattableString.Invariant($"-{(long)window.TotalSeconds} seconds"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<int> CountRecentSignInFailuresAsync(string usernameDigest, long? userId, TimeSpan window, CancellationToken ct = default)
    {
        await using var command = Command("""
            SELECT count(*) FROM audit_log f
             WHERE f.action = 'AUTH_LOGIN_FAILED'
               AND json_extract(f.after_json, '$.username_digest') = $digest
               AND f.occurred_utc > strftime('%Y-%m-%dT%H:%M:%fZ', 'now', $window)
               AND NOT EXISTS (
                   SELECT 1 FROM audit_log s
                    WHERE s.seq > f.seq AND $user IS NOT NULL
                      AND ((s.action = 'AUTH_LOGIN' AND s.actor_kind = 'user' AND s.actor_id = $user)
                           OR (s.table_name = 'app_user' AND s.row_id = $user)));
            """);
        command.Parameters.AddWithValue("$digest", usernameDigest);
        command.Parameters.AddWithValue("$user", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("$window", FormattableString.Invariant($"-{(long)window.TotalSeconds} seconds"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task SetPinHashAsync(long employeeId, string pinHash, bool mustChange, CancellationToken ct = default)
    {
        await using var command = Command("UPDATE employee SET pin_hash = $pin, pin_must_change = $must WHERE id = $id;");
        command.Parameters.AddWithValue("$pin", pinHash);
        command.Parameters.AddWithValue("$must", mustChange ? 1 : 0);
        command.Parameters.AddWithValue("$id", employeeId);
        await ExpectOneRowAsync(command, $"Employee {employeeId}", ct);
    }

    public async Task SetEmployeeActiveAsync(long employeeId, bool isActive, CancellationToken ct = default)
    {
        await using var command = Command("UPDATE employee SET is_active = $active WHERE id = $id;");
        command.Parameters.AddWithValue("$active", isActive ? 1 : 0);
        command.Parameters.AddWithValue("$id", employeeId);
        await ExpectOneRowAsync(command, $"Employee {employeeId}", ct);
    }

    public async Task<DateTimeOffset> GetDatabaseUtcNowAsync(CancellationToken ct = default)
    {
        await using var command = Command("SELECT strftime('%Y-%m-%dT%H:%M:%fZ', 'now');");
        return SqliteTime.Parse((string)(await command.ExecuteScalarAsync(ct))!);
    }

    public async Task<Punch?> FindLatestPunchAsync(long employeeId, CancellationToken ct = default)
    {
        // Effective punches only: a punch superseded by a correction no longer counts.
        await using var command = Command($"""
            SELECT {PunchColumns} FROM punch_effective_v
            WHERE employee_id = $id
            ORDER BY occurred_utc DESC, direction = 'IN' DESC, id DESC
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$id", employeeId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapPunch(reader) : null;
    }

    public async Task<Punch> AppendKioskPunchAsync(
        long employeeId, PunchDirection direction, DateTimeOffset occurredAtUtc, int utcOffsetMinutes, CancellationToken ct = default)
    {
        // recorded_utc is omitted on purpose: the schema fills it from the database clock and
        // rejects a caller-supplied value.
        await using var command = Command($"""
            INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source)
            VALUES ($employee, $direction, $occurred, $offset, 'kiosk')
            RETURNING {PunchColumns};
            """);
        command.Parameters.AddWithValue("$employee", employeeId);
        command.Parameters.AddWithValue("$direction", ToDb(direction));
        command.Parameters.AddWithValue("$occurred", SqliteTime.ToText(occurredAtUtc));
        command.Parameters.AddWithValue("$offset", utcOffsetMinutes);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return MapPunch(reader);
    }

    public async Task<Punch?> FindEffectivePunchAsync(long punchId, CancellationToken ct = default)
    {
        await using var command = Command($"SELECT {PunchColumns} FROM punch_effective_v WHERE id = $id;");
        command.Parameters.AddWithValue("$id", punchId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapPunch(reader) : null;
    }

    public async Task<IReadOnlyList<Punch>> ListEffectivePunchesAsync(
        long employeeId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        await using var command = Command($"""
            SELECT {PunchColumns} FROM punch_effective_v
            WHERE employee_id = $id AND occurred_utc >= $from AND occurred_utc < $to
            ORDER BY occurred_utc, direction = 'IN', id;
            """);
        command.Parameters.AddWithValue("$id", employeeId);
        command.Parameters.AddWithValue("$from", SqliteTime.ToText(fromUtc));
        command.Parameters.AddWithValue("$to", SqliteTime.ToText(toUtc));
        return await ReadAllAsync(command, MapPunch, ct);
    }

    public async Task<IReadOnlyList<(long PunchId, string Kind)>> ListPunchExceptionsAsync(long employeeId, CancellationToken ct = default)
    {
        await using var command = Command("SELECT punch_id, kind FROM punch_exception_v WHERE employee_id = $id;");
        command.Parameters.AddWithValue("$id", employeeId);
        return await ReadAllAsync(command, r => (r.GetInt64(0), r.GetString(1)), ct);
    }

    public async Task<long> AddCorrectionAsync(NewCorrection correction, CancellationToken ct = default)
    {
        await using var command = Command("""
            INSERT INTO punch_correction (action, employee_id, target_punch_id, new_direction,
                                          new_occurred_utc, new_utc_offset_minutes, reason, actor_user_id)
            VALUES ($action, $employee, $target, $direction, $occurred, $offset, $reason, $actor)
            RETURNING id;
            """);
        command.Parameters.AddWithValue("$action", correction.Action switch
        {
            CorrectionAction.Add => "add",
            CorrectionAction.Adjust => "adjust",
            CorrectionAction.Void => "void",
            var other => throw new ArgumentOutOfRangeException(nameof(correction), other, null),
        });
        command.Parameters.AddWithValue("$employee", correction.EmployeeId);
        command.Parameters.AddWithValue("$target", (object?)correction.TargetPunchId ?? DBNull.Value);
        command.Parameters.AddWithValue("$direction", correction.NewDirection is { } d ? ToDb(d) : DBNull.Value);
        command.Parameters.AddWithValue("$occurred", correction.NewOccurredAtUtc is { } t ? SqliteTime.ToText(t) : DBNull.Value);
        command.Parameters.AddWithValue("$offset", (object?)correction.NewUtcOffsetMinutes ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", correction.Reason);
        command.Parameters.AddWithValue("$actor", correction.ActorUserId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<ShiftRecord>> ListShiftsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        await using var command = Command($"""
            SELECT {Prefixed("i")}, {Prefixed("o")}
              FROM shift_v s
              JOIN punch i ON i.id = s.in_punch_id
              LEFT JOIN punch o ON o.id = s.out_punch_id
             WHERE s.in_utc >= $from AND s.in_utc < $to
             ORDER BY s.employee_id, s.in_utc, s.in_punch_id;
            """);
        command.Parameters.AddWithValue("$from", SqliteTime.ToText(fromUtc));
        command.Parameters.AddWithValue("$to", SqliteTime.ToText(toUtc));
        return await ReadAllAsync(command, r =>
        {
            var punchIn = MapPunch(r);
            return new ShiftRecord(punchIn.EmployeeId, punchIn, r.IsDBNull(PunchColumnCount) ? null : MapPunch(r, PunchColumnCount));
        }, ct);
    }

    public async Task<IReadOnlyList<PunchExportRow>> ListPunchesForExportAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        await using var command = Command($"""
            SELECT {Prefixed("p")}, e.first_name || ' ' || e.last_name, e.legacy_id, c.id, p.correction_id
              FROM punch p
              JOIN employee e ON e.id = p.employee_id
              LEFT JOIN punch_correction c ON c.target_punch_id = p.id
             WHERE p.occurred_utc >= $from AND p.occurred_utc < $to
             ORDER BY e.last_name COLLATE NOCASE, e.first_name COLLATE NOCASE, p.employee_id, p.occurred_utc, p.direction = 'IN', p.id;
            """);
        command.Parameters.AddWithValue("$from", SqliteTime.ToText(fromUtc));
        command.Parameters.AddWithValue("$to", SqliteTime.ToText(toUtc));
        const int n = PunchColumnCount;
        return await ReadAllAsync(command, r => new PunchExportRow(
            MapPunch(r),
            r.GetString(n),
            r.IsDBNull(n + 1) ? null : r.GetInt64(n + 1),
            r.IsDBNull(n + 2) ? null : r.GetInt64(n + 2),
            r.IsDBNull(n + 3) ? null : r.GetInt64(n + 3)), ct);
    }

    public async Task<IReadOnlyList<CorrectionExportRow>> ListCorrectionsForExportAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        await using var command = Command($"""
            SELECT c.id, c.created_utc, c.action, c.employee_id, e.first_name || ' ' || e.last_name, e.legacy_id,
                   c.new_direction, c.new_occurred_utc, c.new_utc_offset_minutes, c.reason, u.username, u.display_name,
                   {Prefixed("t")}
              FROM punch_correction c
              JOIN employee e ON e.id = c.employee_id
              JOIN app_user u ON u.id = c.actor_user_id
              LEFT JOIN punch t ON t.id = c.target_punch_id
             WHERE c.created_utc >= $from AND c.created_utc < $to
             ORDER BY c.created_utc, c.id;
            """);
        command.Parameters.AddWithValue("$from", SqliteTime.ToText(fromUtc));
        command.Parameters.AddWithValue("$to", SqliteTime.ToText(toUtc));
        return await ReadAllAsync(command, r => new CorrectionExportRow(
            r.GetInt64(0),
            SqliteTime.Parse(r.GetString(1)),
            r.GetString(2) switch
            {
                "add" => CorrectionAction.Add,
                "adjust" => CorrectionAction.Adjust,
                "void" => CorrectionAction.Void,
                var other => throw new InvalidDataException($"Unknown correction action '{other}'."),
            },
            r.GetInt64(3),
            r.GetString(4),
            r.IsDBNull(5) ? null : r.GetInt64(5),
            r.IsDBNull(12) ? null : MapPunch(r, 12),
            r.IsDBNull(6) ? null : FromDb(r.GetString(6)),
            r.IsDBNull(7) ? null : SqliteTime.Parse(r.GetString(7)),
            r.IsDBNull(8) ? null : r.GetInt32(8),
            r.GetString(9),
            r.GetString(10),
            r.GetString(11)), ct);
    }

    public async Task<AppUser?> FindUserAsync(long userId, CancellationToken ct = default)
    {
        await using var command = Command($"SELECT {UserColumns} FROM app_user WHERE id = $id;");
        command.Parameters.AddWithValue("$id", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapUser(reader) : null;
    }

    public async Task<AppUser?> FindUserByUsernameAsync(string username, CancellationToken ct = default)
    {
        // username is COLLATE NOCASE in the schema.
        await using var command = Command($"SELECT {UserColumns} FROM app_user WHERE username = $username;");
        command.Parameters.AddWithValue("$username", username);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapUser(reader) : null;
    }

    public async Task<IReadOnlyList<AppUser>> ListUsersAsync(CancellationToken ct = default)
    {
        await using var command = Command($"SELECT {UserColumns} FROM app_user ORDER BY id;");
        return await ReadAllAsync(command, MapUser, ct);
    }

    public async Task<long> AddUserAsync(NewAppUser user, CancellationToken ct = default)
    {
        await using var command = Command("""
            INSERT INTO app_user (username, display_name, role, password_hash, employee_id, must_change_password)
            VALUES ($username, $display, $role, $password, $employee, $must)
            RETURNING id;
            """);
        command.Parameters.AddWithValue("$employee", (object?)user.EmployeeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$must", user.MustChangePassword ? 1 : 0);
        command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$display", user.DisplayName);
        command.Parameters.AddWithValue("$role", user.Role switch
        {
            UserRole.Admin => "admin",
            UserRole.Manager => "manager",
            var other => throw new ArgumentOutOfRangeException(nameof(user), other, "Service accounts are created by the schema."),
        });
        command.Parameters.AddWithValue("$password", user.PasswordHash);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task SetUserActiveAsync(long userId, bool isActive, CancellationToken ct = default)
    {
        await using var command = Command("UPDATE app_user SET is_active = $active WHERE id = $id;");
        command.Parameters.AddWithValue("$active", isActive ? 1 : 0);
        command.Parameters.AddWithValue("$id", userId);
        await ExpectOneRowAsync(command, $"Account {userId}", ct);
    }

    public async Task SetUserPasswordAsync(long userId, string passwordHash, bool mustChange, CancellationToken ct = default)
    {
        await using var command = Command("UPDATE app_user SET password_hash = $password, must_change_password = $must WHERE id = $id;");
        command.Parameters.AddWithValue("$password", passwordHash);
        command.Parameters.AddWithValue("$must", mustChange ? 1 : 0);
        command.Parameters.AddWithValue("$id", userId);
        await ExpectOneRowAsync(command, $"Account {userId}", ct);
    }

    public async Task SetUserEmployeeAsync(long userId, long? employeeId, CancellationToken ct = default)
    {
        await using var command = Command("UPDATE app_user SET employee_id = $employee WHERE id = $id;");
        command.Parameters.AddWithValue("$employee", (object?)employeeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", userId);
        await ExpectOneRowAsync(command, $"Account {userId}", ct);
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        await transaction.CommitAsync(ct);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        // Disposing an uncommitted SqliteTransaction rolls it back.
        await transaction.DisposeAsync();
        await connection.DisposeAsync();
        _completed = true;
    }

    private SqliteCommand Command(string sql)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static async Task ExpectOneRowAsync(SqliteCommand command, string what, CancellationToken ct)
    {
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException($"{what} does not exist.");
        }
    }

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(SqliteCommand command, Func<SqliteDataReader, T> map, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static Employee MapEmployee(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3) == 1,
        reader.GetString(4),
        reader.GetInt64(5) == 1,
        reader.IsDBNull(6) ? null : reader.GetInt64(6));

    private const int PunchColumnCount = 7;

    /// <summary><see cref="PunchColumns"/> qualified with a table alias, for joins.</summary>
    private static string Prefixed(string alias) =>
        string.Join(", ", PunchColumns.Split(", ").Select(c => $"{alias}.{c}"));

    private static PunchDirection FromDb(string direction) => direction switch
    {
        "IN" => PunchDirection.In,
        "OUT" => PunchDirection.Out,
        var other => throw new InvalidDataException($"Unknown punch direction '{other}'."),
    };

    private static Punch MapPunch(SqliteDataReader reader) => MapPunch(reader, 0);

    /// <param name="at">Ordinal of the first of the <see cref="PunchColumns"/>.</param>
    private static Punch MapPunch(SqliteDataReader reader, int at) => new(
        reader.GetInt64(at),
        reader.GetInt64(at + 1),
        FromDb(reader.GetString(at + 2)),
        SqliteTime.Parse(reader.GetString(at + 3)),
        reader.GetInt32(at + 4),
        SqliteTime.Parse(reader.GetString(at + 5)),
        reader.GetString(at + 6) switch
        {
            "kiosk" => PunchSource.Kiosk,
            "correction" => PunchSource.Correction,
            "legacy_import" => PunchSource.LegacyImport,
            var other => throw new InvalidDataException($"Unknown punch source '{other}'."),
        });

    private static AppUser MapUser(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3) switch
        {
            "system" => UserRole.System,
            "migration" => UserRole.Migration,
            "admin" => UserRole.Admin,
            "manager" => UserRole.Manager,
            var other => throw new InvalidDataException($"Unknown role '{other}'."),
        },
        reader.GetInt64(4) == 1,
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.GetInt64(7) == 1);

    private static string ToDb(PunchDirection direction) => direction switch
    {
        PunchDirection.In => "IN",
        PunchDirection.Out => "OUT",
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
    };
}
