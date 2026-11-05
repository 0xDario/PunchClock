using System.Globalization;
using PunchClock.Core.Accounts;
using PunchClock.Core.Employees;
using PunchClock.Core.Punches;
using PunchClock.Core.Reports;
using PunchClock.Core.Site;
using PunchClock.Data.Sqlite;

namespace PunchClock.SeedShifts;

/// <summary>
/// Developer tool: fills a PunchClock v2 database with randomized test shifts for one month, so
/// reports can be compared across app versions against a known data set. Every punch is written
/// through <see cref="PunchCorrectionService"/> as an audited "add" correction by a named admin or
/// manager account, with a reason that marks it as test data; nothing bypasses the schema's
/// triggers. Never run it against a site's real database.
/// <code>
/// PunchClock.SeedShifts [options]            print the plan (dry run)
/// PunchClock.SeedShifts [options] --apply    write it
///
///   --db &lt;path&gt;          database; default PUNCHCLOCK_DB or %ProgramData%\PunchClock\punchclock.db
///   --month &lt;yyyy-MM&gt;    month to fill; default last month
///   --employee &lt;id&gt;      employee to seed; repeat for several; default every active employee
///   --as &lt;username&gt;      admin or manager account the corrections are attributed to; default the only active admin
///   --in &lt;HH:mm-HH:mm&gt;   punch-in window, site wall time; default 05:00-07:00
///   --out &lt;HH:mm-HH:mm&gt;  punch-out window; default 14:00-16:00
///   --weekends           include Saturdays and Sundays
///   --seed &lt;int&gt;         random seed; default derived from the month, so a dry run and the real run agree
///   --csv &lt;path&gt;         also write the planned shifts as CSV (employee, date, in, out, hours)
/// </code>
/// Refuses to write when the month has not ended, when an employee already has a punch in the
/// month, or when the account cannot correct the employee (not a manager, or linked to them).
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
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(Options options)
    {
        var database = new SqliteDatabase(options.DbPath, $"{Environment.MachineName}/PunchClock SeedShifts");
        var store = new SqlitePunchClockStore(database);
        Console.WriteLine($"Database: {database.Path}");

        if (new DateTimeOffset(options.MonthEnd, TimeSpan.Zero).AddDays(1) > DateTimeOffset.UtcNow)
        {
            Console.Error.WriteLine($"{options.MonthStart:MMMM yyyy} has not ended yet; the app refuses future punches.");
            return 2;
        }

        AppUser actor;
        List<Employee> employees;
        await using (var uow = await store.BeginAsync())
        {
            var users = await uow.ListUsersAsync();
            if (options.ActorUsername is { } username)
            {
                actor = users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"No account named '{username}'.");
            }
            else
            {
                var admins = users.Where(u => u.Role == UserRole.Admin && u.IsActive).ToList();
                actor = admins.Count == 1
                    ? admins[0]
                    : throw new InvalidOperationException($"{admins.Count} active admin accounts; pass --as <username>.");
            }

            var all = await uow.ListEmployeesAsync(activeOnly: false);
            employees = options.EmployeeIds.Count == 0
                ? all.Where(e => e.IsActive).ToList()
                : options.EmployeeIds.Select(id => all.FirstOrDefault(e => e.Id == id)
                    ?? throw new InvalidOperationException($"No employee with id {id}.")).ToList();
            if (employees.Count == 0)
            {
                throw new InvalidOperationException("No active employees to seed.");
            }

            // The service would refuse these too, one punch at a time; say so before planning.
            var (fromUtc, toUtc) = SiteTime.CoveringUtcRange(options.MonthStart, options.MonthEnd.AddDays(-1));
            foreach (var employee in employees)
            {
                if (actor.EmployeeId == employee.Id)
                {
                    throw new InvalidOperationException($"{actor.Username} is linked to {employee.DisplayName} and cannot correct their punches.");
                }

                var inMonth = (await uow.ListEffectivePunchesAsync(employee.Id, fromUtc, toUtc))
                    .Count(p => p.OccurredAtLocal.Date >= options.MonthStart && p.OccurredAtLocal.Date < options.MonthEnd);
                if (inMonth > 0)
                {
                    throw new InvalidOperationException(
                        $"{employee.DisplayName} already has {inMonth} punches in {options.MonthStart:MMMM yyyy}; refusing to seed on top of them.");
                }
            }
        }

        var shifts = Plan(options, employees);
        Print(shifts, employees);
        if (options.CsvPath is { } csvPath)
        {
            File.WriteAllBytes(csvPath, ToCsv(shifts));
            Console.WriteLine($"Wrote {Path.GetFullPath(csvPath)}");
        }

        Console.WriteLine($"Acting as {actor.Username} ({actor.Role}).");
        if (!options.Apply)
        {
            Console.WriteLine("Dry run; pass --apply to write these punches.");
            return 0;
        }

        var reason = $"Test data: seeded {options.MonthStart:MMMM yyyy} shift for report testing";
        var corrections = new PunchCorrectionService(store);
        var punches = shifts
            .SelectMany(s => new[] { (s.Employee, Direction: PunchDirection.In, At: s.In), (s.Employee, Direction: PunchDirection.Out, At: s.Out) })
            .OrderBy(p => p.At).ThenBy(p => p.Employee.Id)
            .ToList();
        var written = 0;
        foreach (var (employee, direction, at) in punches)
        {
            var result = await corrections.AddAsync(actor, employee.Id, direction, at, reason);
            if (result != CorrectionResult.Corrected)
            {
                throw new InvalidOperationException(
                    $"Punch {direction} for {employee.DisplayName} at {at:yyyy-MM-dd HH:mm:ss} refused: {result}. {written} punches were already written.");
            }

            written++;
        }

        Console.WriteLine($"Wrote {written} punches ({shifts.Count} shifts).");

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

    private static List<Shift> Plan(Options options, IReadOnlyList<Employee> employees)
    {
        var rng = new Random(options.Seed);
        var shifts = new List<Shift>();
        for (var day = options.MonthStart; day < options.MonthEnd; day = day.AddDays(1))
        {
            if (!options.Weekends && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            foreach (var employee in employees)
            {
                shifts.Add(new Shift(employee, day + options.InWindow.Pick(rng), day + options.OutWindow.Pick(rng)));
            }
        }

        return shifts;
    }

    private static void Print(IReadOnlyList<Shift> shifts, IReadOnlyList<Employee> employees)
    {
        var width = employees.Max(e => e.DisplayName.Length);
        foreach (var s in shifts)
        {
            Console.WriteLine($"{s.Employee.DisplayName.PadRight(width)}  {s.In:ddd yyyy-MM-dd}  in {s.In:HH:mm:ss}  out {s.Out:HH:mm:ss}  {s.Hours,5:F2} h");
        }

        foreach (var employee in employees)
        {
            var mine = shifts.Where(s => s.Employee.Id == employee.Id).ToList();
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

    private sealed record Shift(Employee Employee, DateTime In, DateTime Out)
    {
        public double Hours => (Out - In).TotalHours;
    }

    /// <summary>A wall-clock window, inclusive, to the second.</summary>
    private sealed record Window(TimeSpan Start, TimeSpan End)
    {
        public static Window Parse(string text, string option)
        {
            var parts = text.Split('-');
            if (parts.Length != 2
                || !TimeSpan.TryParseExact(parts[0], @"hh\:mm", CultureInfo.InvariantCulture, out var start)
                || !TimeSpan.TryParseExact(parts[1], @"hh\:mm", CultureInfo.InvariantCulture, out var end)
                || end < start)
            {
                throw new ArgumentException($"{option} must be HH:mm-HH:mm with the end no earlier than the start, not '{text}'.");
            }

            return new Window(start, end);
        }

        public TimeSpan Pick(Random rng) =>
            Start + TimeSpan.FromSeconds(rng.Next(0, (int)(End - Start).TotalSeconds + 1));
    }

    private sealed class Options
    {
        public string DbPath { get; private set; } = PunchClockDatabase.ResolvePath();

        public DateTime MonthStart { get; private set; } = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);

        public DateTime MonthEnd => MonthStart.AddMonths(1);

        public List<long> EmployeeIds { get; } = [];

        public string? ActorUsername { get; private set; }

        public Window InWindow { get; private set; } = new(new TimeSpan(5, 0, 0), new TimeSpan(7, 0, 0));

        public Window OutWindow { get; private set; } = new(new TimeSpan(14, 0, 0), new TimeSpan(16, 0, 0));

        public bool Weekends { get; private set; }

        public int? SeedOverride { get; private set; }

        public int Seed => SeedOverride ?? MonthStart.Year * 100 + MonthStart.Month;

        public string? CsvPath { get; private set; }

        public bool Apply { get; private set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                string Value()
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException($"{args[i]} needs a value.");
                    }

                    return args[++i];
                }

                switch (args[i])
                {
                    case "--db":
                        options.DbPath = Path.GetFullPath(Value());
                        break;
                    case "--month":
                        options.MonthStart = DateTime.TryParseExact(Value(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
                            ? month
                            : throw new ArgumentException($"--month must be yyyy-MM, not '{args[i]}'.");
                        break;
                    case "--employee":
                        options.EmployeeIds.Add(long.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                            ? id
                            : throw new ArgumentException($"--employee must be an employee id, not '{args[i]}'."));
                        break;
                    case "--as":
                        options.ActorUsername = Value();
                        break;
                    case "--in":
                        options.InWindow = Window.Parse(Value(), "--in");
                        break;
                    case "--out":
                        options.OutWindow = Window.Parse(Value(), "--out");
                        break;
                    case "--weekends":
                        options.Weekends = true;
                        break;
                    case "--seed":
                        options.SeedOverride = int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed)
                            ? seed
                            : throw new ArgumentException($"--seed must be an integer, not '{args[i]}'.");
                        break;
                    case "--csv":
                        options.CsvPath = Value();
                        break;
                    case "--apply":
                        options.Apply = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{args[i]}'.");
                }
            }

            if (options.OutWindow.Start <= options.InWindow.End)
            {
                throw new ArgumentException("The --out window must start after the --in window ends, so every shift has a positive length.");
            }

            return options;
        }
    }
}
