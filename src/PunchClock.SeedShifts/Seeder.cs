using PunchClock.Core.Accounts;
using PunchClock.Core.Employees;
using PunchClock.Core.Persistence;
using PunchClock.Core.Punches;
using PunchClock.Core.Site;

namespace PunchClock.SeedShifts;

/// <summary>A refusal with the message to print and the process exit code.</summary>
internal sealed class SeedException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>One planned shift: site wall times and the UTC instants they resolve to.</summary>
internal sealed record Shift(Employee Employee, DateTime In, DateTime Out, DateTimeOffset InUtc, DateTimeOffset OutUtc)
{
    /// <summary>Elapsed hours, so a shift across a daylight-saving change counts the real time worked.</summary>
    public double Hours => (OutUtc - InUtc).TotalHours;
}

internal sealed record SeedPlan(
    AppUser Actor, IReadOnlyList<Employee> Employees, TimeZoneInfo Zone, DateTime MonthStart, IReadOnlyList<Shift> Shifts);

internal static class Seeder
{
    /// <summary>
    /// Plans the month and checks every punch in it against the rules
    /// <see cref="PunchCorrectionService"/> applies one punch at a time, so that a plan returned
    /// here can be written in full and a refused one writes nothing.
    /// </summary>
    public static async Task<SeedPlan> PlanAsync(IPunchClockStore store, Options options, CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);

        var zoneId = await uow.GetSettingAsync(SiteSettingKeys.TimeZoneId, ct);
        if (zoneId is null or SiteSettingKeys.Unset || !TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone))
        {
            throw new SeedException("The site time zone is not set (or unknown on this machine); set it in the app first.");
        }

        var nowUtc = await uow.GetDatabaseUtcNowAsync(ct);
        var siteToday = TimeZoneInfo.ConvertTime(nowUtc, zone).Date;
        var monthStart = options.Month ?? new DateTime(siteToday.Year, siteToday.Month, 1).AddMonths(-1);
        if (!MonthHasEnded(monthStart, nowUtc, zone))
        {
            throw new SeedException(
                $"{monthStart:MMMM yyyy} has not ended yet in site time ({zone.Id}); the app refuses future punches.", exitCode: 2);
        }

        var actor = await FindActorAsync(uow, options.ActorUsername, ct);
        var employees = await FindEmployeesAsync(uow, options.EmployeeIds, ct);

        // The service would refuse these too, one punch at a time, after earlier punches were written.
        var monthEnd = monthStart.AddMonths(1);
        var (fromUtc, toUtc) = SiteTime.CoveringUtcRange(monthStart, monthEnd.AddDays(-1));
        foreach (var employee in employees)
        {
            if (actor.EmployeeId == employee.Id)
            {
                throw new SeedException($"{actor.Username} is linked to {employee.DisplayName} and cannot correct their punches.");
            }

            var inMonth = (await uow.ListEffectivePunchesAsync(employee.Id, fromUtc, toUtc, ct))
                .Count(p => p.OccurredAtLocal.Date >= monthStart && p.OccurredAtLocal.Date < monthEnd);
            if (inMonth > 0)
            {
                throw new SeedException(
                    $"{employee.DisplayName} already has {inMonth} punches in {monthStart:MMMM yyyy}; refusing to seed on top of them.");
            }
        }

        var shifts = Plan(options, employees, monthStart, zone, nowUtc);
        return new SeedPlan(actor, employees, zone, monthStart, shifts);
    }

    /// <summary>Writes the plan's punches in time order. Returns how many were written.</summary>
    public static async Task<int> ApplyAsync(IPunchClockStore store, SeedPlan plan, CancellationToken ct = default)
    {
        var reason = $"Test data: seeded {plan.MonthStart:MMMM yyyy} shift for report testing";
        var corrections = new PunchCorrectionService(store);
        var punches = plan.Shifts
            .SelectMany(s => new[] { (s.Employee, Direction: PunchDirection.In, Local: s.In, Utc: s.InUtc), (s.Employee, Direction: PunchDirection.Out, Local: s.Out, Utc: s.OutUtc) })
            .OrderBy(p => p.Utc).ThenBy(p => p.Employee.Id)
            .ToList();
        var written = 0;
        foreach (var (employee, direction, local, _) in punches)
        {
            var result = await corrections.AddAsync(plan.Actor, employee.Id, direction, local, reason, ct);
            if (result != CorrectionResult.Corrected)
            {
                // Not expected after PlanAsync; only a concurrent change to the database gets here.
                throw new SeedException(
                    $"Punch {direction} for {employee.DisplayName} at {local:yyyy-MM-dd HH:mm:ss} refused: {result}. " +
                    $"{written} punches were already written; void them in the app or discard this test database.");
            }

            written++;
        }

        return written;
    }

    /// <summary>True once the site's calendar has reached the first day of the following month.</summary>
    public static bool MonthHasEnded(DateTime monthStart, DateTimeOffset nowUtc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(nowUtc, zone).Date >= monthStart.AddMonths(1);

    private static List<Shift> Plan(Options options, IReadOnlyList<Employee> employees, DateTime monthStart, TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        var rng = new Random(options.SeedFor(monthStart));
        var shifts = new List<Shift>();
        var problems = new List<string>();
        for (var day = monthStart; day < monthStart.AddMonths(1); day = day.AddDays(1))
        {
            if (!options.Weekends && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            foreach (var employee in employees)
            {
                var inLocal = day + options.InWindow.Pick(rng);
                var outLocal = day + options.OutWindow.Pick(rng);
                var inUtc = Resolve(employee, PunchDirection.In, inLocal);
                var outUtc = Resolve(employee, PunchDirection.Out, outLocal);
                if (inUtc is { } i && outUtc is { } o)
                {
                    if (o <= i)
                    {
                        problems.Add($"{employee.DisplayName} {day:yyyy-MM-dd}: punch-out {outLocal:HH:mm:ss} is not after punch-in {inLocal:HH:mm:ss} in elapsed time.");
                    }

                    shifts.Add(new Shift(employee, inLocal, outLocal, i, o));
                }
            }
        }

        if (problems.Count > 0)
        {
            const int Shown = 10;
            var more = problems.Count > Shown ? $"{Environment.NewLine}  ...and {problems.Count - Shown} more" : "";
            throw new SeedException(
                $"Nothing was written; {problems.Count} planned punches would be refused:{Environment.NewLine}  " +
                string.Join(Environment.NewLine + "  ", problems.Take(Shown)) + more +
                $"{Environment.NewLine}Choose --in/--out windows that exist every day at the site, or leave out the affected days.");
        }

        return shifts;

        // The same conversion the correction service applies, so what passes here is what it stores.
        DateTimeOffset? Resolve(Employee employee, PunchDirection direction, DateTime local)
        {
            var utc = PunchCorrectionService.ToUtc(local, zone);
            if (utc is null)
            {
                problems.Add($"{employee.DisplayName} {direction} {local:yyyy-MM-dd HH:mm:ss} does not exist in {zone.Id} (skipped by a daylight-saving change).");
            }
            else if (utc > nowUtc)
            {
                problems.Add($"{employee.DisplayName} {direction} {local:yyyy-MM-dd HH:mm:ss} is in the future.");
                return null;
            }

            return utc;
        }
    }

    private static async Task<AppUser> FindActorAsync(IPunchClockUnitOfWork uow, string? username, CancellationToken ct)
    {
        var users = await uow.ListUsersAsync(ct);
        AppUser actor;
        if (username is not null)
        {
            actor = users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase))
                ?? throw new SeedException($"No account named '{username}'.");
        }
        else
        {
            var admins = users.Where(u => u.Role == UserRole.Admin && u.IsActive).ToList();
            actor = admins.Count == 1
                ? admins[0]
                : throw new SeedException($"{admins.Count} active admin accounts; pass --as <username>.");
        }

        if (actor.Role is not (UserRole.Admin or UserRole.Manager) || !actor.IsActive)
        {
            throw new SeedException($"{actor.Username} is not an active manager or admin and cannot correct punches.");
        }

        return actor;
    }

    private static async Task<List<Employee>> FindEmployeesAsync(IPunchClockUnitOfWork uow, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var all = await uow.ListEmployeesAsync(activeOnly: false, ct);
        var employees = ids.Count == 0
            ? all.Where(e => e.IsActive).ToList()
            : ids.Distinct().Select(id => all.FirstOrDefault(e => e.Id == id)
                ?? throw new SeedException($"No employee with id {id}.")).ToList();
        return employees.Count > 0 ? employees : throw new SeedException("No active employees to seed.");
    }
}
