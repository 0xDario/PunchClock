#if DEBUG
using PunchClock.Core.Audit;
using PunchClock.Core.Employees;
using PunchClock.Data.Sqlite;

namespace PunchClock.App;

/// <summary>
/// Debug builds only, and only on request: gives an empty scratch database two employees with
/// well-known PINs so the kiosk can be tried. Needs both <c>PUNCHCLOCK_DEMO=1</c> and an explicit
/// <c>PUNCHCLOCK_DB</c>, so trying a Debug build never leaves known PINs in the real database.
/// </summary>
internal static class DemoData
{
    public const string Variable = "PUNCHCLOCK_DEMO";

    public static async Task SeedIfRequestedAsync(EmployeeService employees)
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PunchClockDatabase.PathVariable))
            || (await employees.ListAsync(activeOnly: false)).Count > 0)
        {
            return;
        }

        await employees.CreateAsync(AuditActor.System, "Demo", "Employee", "1234", pinMustChange: false);
        await employees.CreateAsync(AuditActor.System, "Second", "Tester", "0042", pinMustChange: false);
    }
}
#endif
