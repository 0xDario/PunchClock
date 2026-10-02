using Microsoft.Data.Sqlite;
using PunchClock.Core.Employees;
using PunchClock.Core.Persistence;
using PunchClock.Core.Punches;

namespace PunchClock.Data.Sqlite;

public sealed class SqlitePunchClockStore(SqliteDatabase database) : IPunchClockStore
{
    public async Task<IPunchClockUnitOfWork> BeginAsync(CancellationToken ct = default)
    {
        var connection = await database.OpenAsync(ct);
        try
        {
            // BEGIN IMMEDIATE: take the write lock up front so a read-check-insert sequence
            // cannot interleave with another writer. Contention waits up to DefaultTimeout.
            var transaction = connection.BeginTransaction(deferred: false);
            return new SqliteUnitOfWork(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal sealed class SqliteUnitOfWork(SqliteConnection connection, SqliteTransaction transaction) : IPunchClockUnitOfWork
{
    private const string EmployeeColumns = "id, first_name, last_name, is_active, pin_hash";
    private const string PunchColumns = "id, employee_id, direction, occurred_at_utc, utc_offset_minutes, recorded_at_utc, source";

    private bool _completed;

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
        await using var reader = await command.ExecuteReaderAsync(ct);
        var employees = new List<Employee>();
        while (await reader.ReadAsync(ct))
        {
            employees.Add(MapEmployee(reader));
        }

        return employees;
    }

    public async Task<long> AddEmployeeAsync(NewEmployee employee, CancellationToken ct = default)
    {
        await using var command = Command("""
            INSERT INTO employee (first_name, last_name, pin_hash, is_active, created_at_utc)
            VALUES ($first, $last, $pin, 1, $created)
            RETURNING id;
            """);
        command.Parameters.AddWithValue("$first", employee.FirstName);
        command.Parameters.AddWithValue("$last", employee.LastName);
        command.Parameters.AddWithValue("$pin", employee.PinHash);
        command.Parameters.AddWithValue("$created", SqliteTime.ToText(employee.CreatedAtUtc));
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task SetPinHashAsync(long employeeId, string pinHash, CancellationToken ct = default)
    {
        await using var command = Command("UPDATE employee SET pin_hash = $pin WHERE id = $id;");
        command.Parameters.AddWithValue("$pin", pinHash);
        command.Parameters.AddWithValue("$id", employeeId);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException($"Employee {employeeId} does not exist.");
        }
    }

    public async Task<Punch?> FindLatestPunchAsync(long employeeId, CancellationToken ct = default)
    {
        await using var command = Command($"""
            SELECT {PunchColumns} FROM punch
            WHERE employee_id = $id
            ORDER BY occurred_at_utc DESC, id DESC
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$id", employeeId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapPunch(reader) : null;
    }

    public async Task<Punch> AppendPunchAsync(NewPunch punch, CancellationToken ct = default)
    {
        await using var command = Command($"""
            INSERT INTO punch (employee_id, direction, occurred_at_utc, utc_offset_minutes, recorded_at_utc, source)
            VALUES ($employee, $direction, $occurred, $offset, $recorded, $source)
            RETURNING {PunchColumns};
            """);
        command.Parameters.AddWithValue("$employee", punch.EmployeeId);
        command.Parameters.AddWithValue("$direction", ToDb(punch.Direction));
        command.Parameters.AddWithValue("$occurred", SqliteTime.ToText(punch.OccurredAtUtc));
        command.Parameters.AddWithValue("$offset", punch.UtcOffsetMinutes);
        command.Parameters.AddWithValue("$recorded", SqliteTime.ToText(punch.RecordedAtUtc));
        command.Parameters.AddWithValue("$source", ToDb(punch.Source));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return MapPunch(reader);
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

    private static Employee MapEmployee(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3) == 1,
        reader.GetString(4));

    private static Punch MapPunch(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2) switch
        {
            "IN" => PunchDirection.In,
            "OUT" => PunchDirection.Out,
            var other => throw new InvalidDataException($"Unknown punch direction '{other}'."),
        },
        SqliteTime.Parse(reader.GetString(3)),
        reader.GetInt32(4),
        SqliteTime.Parse(reader.GetString(5)),
        reader.GetString(6) switch
        {
            "KIOSK" => PunchSource.Kiosk,
            "IMPORT" => PunchSource.Import,
            var other => throw new InvalidDataException($"Unknown punch source '{other}'."),
        });

    private static string ToDb(PunchDirection direction) => direction switch
    {
        PunchDirection.In => "IN",
        PunchDirection.Out => "OUT",
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
    };

    private static string ToDb(PunchSource source) => source switch
    {
        PunchSource.Kiosk => "KIOSK",
        PunchSource.Import => "IMPORT",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };
}
