#if DEBUG
using PunchClock.Core.Employees;

namespace PunchClock.App;

/// <summary>Debug builds only: gives an empty database two employees so the kiosk can be tried.</summary>
internal static class DemoData
{
    public static async Task SeedIfEmptyAsync(EmployeeService employees)
    {
        if ((await employees.ListActiveAsync()).Count > 0)
        {
            return;
        }

        await employees.CreateAsync("Demo", "Employee", "1234");
        await employees.CreateAsync("Second", "Tester", "0042");
    }
}
#endif
