using System.Globalization;
using Microsoft.Data.Sqlite;
using PunchClock.Core.Abstractions;
using PunchClock.Core.Domain;

namespace PunchClock.Data;

public sealed class SqlitePunchStore : IPunchStore
{
    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private const string Columns = "id, employee_id, direction, occurred_utc, utc_offset_minutes, source";

    private readonly SqliteDatabase _database;

    public SqlitePunchStore(SqliteDatabase database) => _database = database;

    public Punch? GetLatest(long employeeId)
    {
        using var connection = _database.Open();
        return GetLatest(connection, null, employeeId);
    }

    public IReadOnlyList<Punch> GetForEmployee(long employeeId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM punch WHERE employee_id = $id ORDER BY occurred_utc, id;";
        command.Parameters.AddWithValue("$id", employeeId);
        using var reader = command.ExecuteReader();
        var punches = new List<Punch>();
        while (reader.Read())
        {
            punches.Add(Map(reader));
        }

        return punches;
    }

    public AppendPunchResult TryAppend(NewPunch punch)
    {
        ArgumentNullException.ThrowIfNull(punch);

        using var connection = _database.Open();

        // BEGIN IMMEDIATE takes the write lock up front, so the state check and the
        // insert cannot interleave with another writer (second kiosk, double click).
        using var transaction = connection.BeginTransaction(deferred: false);

        var stateBefore = PunchRules.StateAfter(GetLatest(connection, transaction, punch.EmployeeId));
        if (!PunchRules.IsAllowed(stateBefore, punch.Direction))
        {
            return new AppendPunchResult(AppendPunchStatus.Rejected, stateBefore, null);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source)
            VALUES ($employee, $direction, $occurred, $offset, $source)
            RETURNING {Columns};
            """;
        command.Parameters.AddWithValue("$employee", punch.EmployeeId);
        command.Parameters.AddWithValue("$direction", ToDb(punch.Direction));
        command.Parameters.AddWithValue("$occurred", punch.OccurredAt.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$offset", (long)punch.OccurredAt.Offset.TotalMinutes);
        command.Parameters.AddWithValue("$source", punch.Source);

        Punch appended;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            appended = Map(reader);
        }

        transaction.Commit();
        return new AppendPunchResult(AppendPunchStatus.Appended, stateBefore, appended);
    }

    private static Punch? GetLatest(SqliteConnection connection, SqliteTransaction? transaction, long employeeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {Columns} FROM punch
            WHERE employee_id = $id
            ORDER BY occurred_utc DESC, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", employeeId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static Punch Map(SqliteDataReader reader)
    {
        var utc = DateTime.ParseExact(
            reader.GetString(3), UtcFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var offset = TimeSpan.FromMinutes(reader.GetInt64(4));
        return new Punch(
            reader.GetInt64(0),
            reader.GetInt64(1),
            FromDb(reader.GetString(2)),
            new DateTimeOffset(utc).ToOffset(offset),
            reader.GetString(5));
    }

    private static string ToDb(PunchDirection direction) => direction switch
    {
        PunchDirection.In => "IN",
        PunchDirection.Out => "OUT",
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    private static PunchDirection FromDb(string value) => value switch
    {
        "IN" => PunchDirection.In,
        "OUT" => PunchDirection.Out,
        _ => throw new InvalidDataException($"Unknown punch direction '{value}'."),
    };
}
