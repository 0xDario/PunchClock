using Microsoft.Data.Sqlite;
using PunchClock.Core.Domain;
using PunchClock.Core.Security;
using PunchClock.Core.Services;
using PunchClock.Data;

namespace PunchClock.Tests;

public sealed class PunchServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 3, 9, 8, 0, 0, TimeSpan.FromHours(-5));

    private readonly TestDatabase _db = new();
    private readonly FakeClock _clock = new(Start);
    private readonly SqliteEmployeeStore _employeeStore;
    private readonly SqlitePunchStore _punchStore;
    private readonly EmployeeService _employees;
    private readonly PunchService _punches;

    public PunchServiceTests()
    {
        var hasher = new Pbkdf2PinHasher(iterations: 1_000);
        _employeeStore = new SqliteEmployeeStore(_db.Database);
        _punchStore = new SqlitePunchStore(_db.Database);
        _employees = new EmployeeService(_employeeStore, hasher);
        _punches = new PunchService(_employeeStore, _punchStore, hasher, _clock);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void New_employee_starts_punched_out_with_no_placeholder_rows()
    {
        var id = _employees.Create("Ada", "Lovelace", "0420");

        Assert.Equal(PunchState.PunchedOut, _punches.GetState(id));
        Assert.Empty(_punchStore.GetForEmployee(id));
    }

    [Fact]
    public void Punch_in_then_out_records_two_events_with_local_offset()
    {
        var id = _employees.Create("Ada", "Lovelace", "0420");

        var punchIn = _punches.PunchIn(id, "0420");
        _clock.Advance(TimeSpan.FromHours(8.5));
        var punchOut = _punches.PunchOut(id, "0420");

        Assert.True(punchIn.Succeeded);
        Assert.True(punchOut.Succeeded);
        Assert.Equal(PunchState.PunchedOut, _punches.GetState(id));

        var stored = _punchStore.GetForEmployee(id);
        Assert.Equal([PunchDirection.In, PunchDirection.Out], stored.Select(p => p.Direction));
        Assert.Equal(Start, stored[0].OccurredAt);
        Assert.Equal(TimeSpan.FromHours(-5), stored[0].OccurredAt.Offset);
        Assert.Equal(PunchService.KioskSource, stored[0].Source);
    }

    [Fact]
    public void Punch_in_twice_is_rejected_not_toggled()
    {
        var id = _employees.Create("Ada", "Lovelace", "0420");
        _punches.PunchIn(id, "0420");

        var second = _punches.PunchIn(id, "0420");

        Assert.Equal(PunchOutcomeStatus.AlreadyPunchedIn, second.Status);
        Assert.Single(_punchStore.GetForEmployee(id));
        Assert.Equal(PunchState.PunchedIn, _punches.GetState(id));
    }

    [Fact]
    public void Punch_out_while_out_is_rejected()
    {
        var id = _employees.Create("Ada", "Lovelace", "0420");

        var outcome = _punches.PunchOut(id, "0420");

        Assert.Equal(PunchOutcomeStatus.NotPunchedIn, outcome.Status);
        Assert.Empty(_punchStore.GetForEmployee(id));
    }

    [Theory]
    [InlineData("0421")]
    [InlineData("420")]
    [InlineData("")]
    [InlineData("abcd")]
    public void Wrong_pin_records_nothing(string pin)
    {
        var id = _employees.Create("Ada", "Lovelace", "0420");

        var outcome = _punches.PunchIn(id, pin);

        Assert.Equal(PunchOutcomeStatus.InvalidPin, outcome.Status);
        Assert.Empty(_punchStore.GetForEmployee(id));
    }

    [Fact]
    public void Unknown_employee_is_rejected()
    {
        Assert.Equal(PunchOutcomeStatus.UnknownEmployee, _punches.PunchIn(999, "1234").Status);
    }

    [Fact]
    public void Inactive_employee_is_rejected_and_hidden_from_kiosk_list()
    {
        var id = _employees.Create("Ada", "Lovelace", "0420");
        using (var connection = _db.Database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE employee SET is_active = 0 WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        Assert.Equal(PunchOutcomeStatus.InactiveEmployee, _punches.PunchIn(id, "0420").Status);
        Assert.DoesNotContain(_employees.GetActive(), e => e.Id == id);
    }

    [Fact]
    public void Change_pin_requires_current_pin()
    {
        var id = _employees.Create("Ada", "Lovelace", "0420");

        Assert.False(_employees.ChangePin(id, "9999", "5555"));
        Assert.True(_employees.ChangePin(id, "0420", "5555"));

        Assert.Equal(PunchOutcomeStatus.InvalidPin, _punches.PunchIn(id, "0420").Status);
        Assert.True(_punches.PunchIn(id, "5555").Succeeded);
    }

    [Fact]
    public void Stored_pin_is_hashed()
    {
        var id = _employees.Create("Ada", "Lovelace", "0420");

        var stored = _employeeStore.GetPinHash(id);

        Assert.NotNull(stored);
        Assert.DoesNotContain("0420", stored, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UPDATE punch SET direction = 'OUT';")]
    [InlineData("DELETE FROM punch;")]
    public void Punch_rows_are_append_only(string sql)
    {
        var id = _employees.Create("Ada", "Lovelace", "0420");
        _punches.PunchIn(id, "0420");

        using var connection = _db.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var error = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Contains("append-only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Concurrent_punch_ins_record_exactly_one()
    {
        var id = _employees.Create("Ada", "Lovelace", "0420");

        var outcomes = Enumerable.Range(0, 8)
            .AsParallel()
            .Select(_ => _punches.PunchIn(id, "0420"))
            .ToList();

        Assert.Single(outcomes, o => o.Succeeded);
        Assert.Single(_punchStore.GetForEmployee(id));
    }
}
