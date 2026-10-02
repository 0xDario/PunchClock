using PunchClock.Core.Employees;

namespace PunchClock.Tests;

public sealed class EmployeeServiceTests : DatabaseTest
{
    [Fact]
    public async Task Stores_a_hash_never_the_pin()
    {
        var id = await Db.AddEmployeeAsync("482193");

        var stored = await Db.ScalarAsync<string>("SELECT pin_hash FROM employee WHERE id = $id;", ("$id", id));

        Assert.StartsWith("pbkdf2-sha256$", stored);
        Assert.DoesNotContain("482193", stored);
        Assert.True(TestDatabase.FastHasher.Verify("482193", stored));
    }

    [Theory]
    [InlineData("Ada", "Lovelace", "12")]
    [InlineData("Ada", "Lovelace", "12ab")]
    [InlineData(" ", "Lovelace", "1234")]
    [InlineData("Ada", "", "1234")]
    public async Task Rejects_invalid_input(string first, string last, string pin)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Db.Employees.CreateAsync(first, last, pin));
        Assert.Equal(0, await Db.ScalarAsync<long>("SELECT count(*) FROM employee;"));
    }

    [Fact]
    public async Task Lists_active_employees_by_last_name()
    {
        await Db.AddEmployeeAsync(first: "Grace", last: "Hopper");
        var retired = await Db.AddEmployeeAsync(first: "Alan", last: "Turing");
        await Db.AddEmployeeAsync(first: "Ada", last: "Lovelace");
        await Db.ExecuteAsync("UPDATE employee SET is_active = 0 WHERE id = $id;", ("$id", retired));

        var names = (await Db.Employees.ListActiveAsync()).Select(e => e.DisplayName);

        Assert.Equal(["Grace Hopper", "Ada Lovelace"], names);
    }

    [Fact]
    public async Task Changing_a_pin_requires_the_current_one()
    {
        var id = await Db.AddEmployeeAsync("1111");

        Assert.Equal(PinChangeResult.InvalidCurrentPin, await Db.Employees.ChangePinAsync(id, "2222", "3333"));
        Assert.Equal(PinChangeResult.NewPinRejected, await Db.Employees.ChangePinAsync(id, "1111", "33"));
        Assert.Equal(PinChangeResult.EmployeeNotFound, await Db.Employees.ChangePinAsync(999, "1111", "3333"));
        Assert.Equal(PinChangeResult.Changed, await Db.Employees.ChangePinAsync(id, "1111", "0333"));

        Assert.False((await Db.Punches.PunchAsync(id, "1111", Core.Punches.PunchDirection.In)).Accepted);
        Assert.True((await Db.Punches.PunchAsync(id, "0333", Core.Punches.PunchDirection.In)).Accepted);
    }
}
