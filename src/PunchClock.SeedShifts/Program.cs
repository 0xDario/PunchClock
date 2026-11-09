using System.Globalization;
using PunchClock.Core.Reports;
using PunchClock.Data.Sqlite;

namespace PunchClock.SeedShifts;

/// <summary>
/// Developer tool: fills a PunchClock v2 database with randomized test shifts for one month, so
/// reports can be compared across app versions against a known data set. Every punch is written
/// through <see cref="Core.Punches.PunchCorrectionService"/> as an audited "add" correction by a
/// named admin or manager account, with a reason that marks it as test data; nothing bypasses the
/// schema's triggers. Never run it against a site's real database.
/// <code>
/// PunchClock.SeedShifts [options]            print the plan (dry run)
/// PunchClock.SeedShifts [options] --apply    write it
///
///   --db &lt;path&gt;          database; default PUNCHCLOCK_DB or %ProgramData%\PunchClock\punchclock.db
///   --month &lt;yyyy-MM&gt;    month to fill; default the month before the current one in site time
///   --employee &lt;id&gt;      employee to seed; repeat for several (each once); default every active employee
///   --as &lt;username&gt;      admin or manager account the corrections are attributed to; default the only active admin
///   --in &lt;HH:mm-HH:mm&gt;   punch-in window, site wall time; default 05:00-07:00
///   --out &lt;HH:mm-HH:mm&gt;  punch-out window; default 14:00-16:00
///   --weekends           include Saturdays and Sundays
///   --seed &lt;int&gt;         random seed; default derived from the month, so a dry run and the real run agree
///   --csv &lt;path&gt;         also write the planned shifts as CSV (employee, date, in, out, hours); never the database
/// </code>
/// The whole plan is checked before the first write, so a refused run writes nothing. It is refused
/// when the site time zone is not set, the month has not ended in site time, an employee already has
/// a punch in the month, the account cannot correct the employee (not an active manager or admin,
/// or linked to them), or a planned time does not exist at the site (skipped by a daylight-saving
/// change).
/// Exit codes: 0 ok, 1 error, 2 usage, 3 audit verification failed after writing.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Usage error: {ex.Message}");
            Console.Error.WriteLine("See the comment at the top of Program.cs for the options.");
            return 2;
        }

        try
        {
            return await RunAsync(options);
        }
        catch (SeedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ex.ExitCode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or Microsoft.Data.Sqlite.SqliteException or SchemaMismatchException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(Options options)
    {
        // Opening a missing file would create an empty one; this tool only fills existing databases.
        if (!File.Exists(options.DbPath))
        {
            throw new SeedException($"No database at {options.DbPath}; run the app once against that path to create it.", exitCode: 2);
        }

        var database = new SqliteDatabase(options.DbPath, $"{Environment.MachineName}/PunchClock SeedShifts");
        var store = new SqlitePunchClockStore(database);
        Console.WriteLine($"Database: {database.Path}");

        var plan = await Seeder.PlanAsync(store, options);
        Print(plan);
        if (options.CsvPath is { } csvPath)
        {
            CsvTarget.EnsureNotDatabase(csvPath, options.DbPath);
            await File.WriteAllBytesAsync(csvPath, ToCsv(plan.Shifts));
            Console.WriteLine($"Wrote {csvPath}");
        }

        Console.WriteLine($"Acting as {plan.Actor.Username} ({plan.Actor.Role}).");
        if (!options.Apply)
        {
            Console.WriteLine("Dry run; pass --apply to write these punches.");
            return 0;
        }

        var written = await Seeder.ApplyAsync(store, plan);
        Console.WriteLine($"Wrote {written} punches ({plan.Shifts.Count} shifts).");

        var problems = await AuditVerifier.FindProblemsAsync(database);
        if (problems.Count == 0)
        {
            Console.WriteLine("Audit verification: OK");
            return 0;
        }

        Console.Error.WriteLine($"Audit verification found {problems.Count} problems:");
        foreach (var problem in problems)
        {
            Console.Error.WriteLine($"  {problem}");
        }

        return 3;
    }

    private static void Print(SeedPlan plan)
    {
        Console.WriteLine($"Site time zone: {plan.Zone.Id}");
        var width = plan.Employees.Max(e => e.DisplayName.Length);
        foreach (var s in plan.Shifts)
        {
            Console.WriteLine($"{s.Employee.DisplayName.PadRight(width)}  {s.In:ddd yyyy-MM-dd}  in {s.In:HH:mm:ss}  out {s.Out:HH:mm:ss}  {s.Hours,5:F2} h");
        }

        foreach (var employee in plan.Employees)
        {
            var mine = plan.Shifts.Where(s => s.Employee.Id == employee.Id).ToList();
            Console.WriteLine($"{employee.DisplayName}: {mine.Count} shifts, {mine.Sum(s => s.Hours):F2} hours");
        }
    }

    private static byte[] ToCsv(IEnumerable<Shift> shifts) =>
        Csv.Build(
            ["employee_id", "employee", "date", "punch_in", "punch_out", "hours"],
            shifts.Select(s => (IReadOnlyList<object?>)
            [
                s.Employee.Id,
                s.Employee.DisplayName,
                s.In.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                s.In.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                s.Out.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                Math.Round(s.Hours, 4),
            ]),
            out _);
}
