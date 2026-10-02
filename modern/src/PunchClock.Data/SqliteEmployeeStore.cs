using Microsoft.Data.Sqlite;
using PunchClock.Core.Abstractions;
using PunchClock.Core.Domain;

namespace PunchClock.Data;

public sealed class SqliteEmployeeStore : IEmployeeStore
{
    private const string Columns = "id, first_name, last_name, is_active";

    private readonly SqliteDatabase _database;

    public SqliteEmployeeStore(SqliteDatabase database) => _database = database;

    public IReadOnlyList<Employee> GetActive()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM employee WHERE is_active = 1 ORDER BY last_name, first_name, id;";
        using var reader = command.ExecuteReader();
        var employees = new List<Employee>();
        while (reader.Read())
        {
            employees.Add(Map(reader));
        }

        return employees;
    }

    public Employee? Find(long employeeId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM employee WHERE id = $id;";
        command.Parameters.AddWithValue("$id", employeeId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public string? GetPinHash(long employeeId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pin_hash FROM employee WHERE id = $id;";
        command.Parameters.AddWithValue("$id", employeeId);
        return command.ExecuteScalar() as string;
    }

    public long Add(string firstName, string lastName, string pinHash)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO employee (first_name, last_name, pin_hash)
            VALUES ($first, $last, $pin)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$first", firstName);
        command.Parameters.AddWithValue("$last", lastName);
        command.Parameters.AddWithValue("$pin", pinHash);
        return (long)command.ExecuteScalar()!;
    }

    public void SetPinHash(long employeeId, string pinHash)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE employee SET pin_hash = $pin WHERE id = $id;";
        command.Parameters.AddWithValue("$pin", pinHash);
        command.Parameters.AddWithValue("$id", employeeId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Employee {employeeId} does not exist.");
        }
    }

    private static Employee Map(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) == 1);
}
