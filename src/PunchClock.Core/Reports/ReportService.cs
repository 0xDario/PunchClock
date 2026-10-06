using System.Security.Cryptography;
using System.Text.Json;
using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;
using PunchClock.Core.Persistence;
using PunchClock.Core.Punches;
using PunchClock.Core.Site;

namespace PunchClock.Core.Reports;

/// <summary>
/// Pay-period hours and CSV exports for admins and managers. Periods are site calendar dates,
/// inclusive. Every export writes its file and then a <c>REPORT_EXPORT</c> audit event naming
/// the file, its row count and SHA-256, so a figure handed to payroll can be traced to the
/// exact file and the person who produced it.
/// </summary>
public sealed class ReportService(IPunchClockStore store)
{
    /// <summary>
    /// Hours per employee by the old Access report's rule, so totals match it during the switch:
    /// a shift counts when its IN is on or after <paramref name="from"/> and its OUT is before the
    /// day after <paramref name="to"/>, both in site wall-clock time, and its minutes are
    /// <see cref="PayRules.AccessDateDiffMinutes"/>. Shifts that start in the period but are not
    /// paid by that rule (no punch-out yet, or ending after the period) are listed, not dropped.
    /// </summary>
    public async Task<PayReport> PayReportAsync(AppUser by, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        EnsureAllowed(by, from, to);
        await using var uow = await store.BeginAsync(ct);
        return await BuildPayReportAsync(uow, from, to, ct);
    }

    public Task<ExportResult> ExportPayReportAsync(AppUser by, DateOnly from, DateOnly to, string path, CancellationToken ct = default) =>
        ExportAsync(by, "pay_report", from, to, path, async uow =>
        {
            var report = await BuildPayReportAsync(uow, from, to, ct);
            var bytes = Csv.Build(
                ["employee_id", "legacy_id", "employee", "shifts", "minutes", "hours", "elapsed_hours", "shifts_not_counted"],
                report.Lines.Select(l => (IReadOnlyList<object?>)
                    [l.EmployeeId, l.LegacyId, l.Name, l.Shifts, l.Minutes, l.Hours, l.ElapsedHours, l.NotCounted]),
                out var rows);
            return (bytes, rows);
        }, ct);

    /// <summary>Every punch whose site-local date is in the period, superseded ones included.</summary>
    public Task<ExportResult> ExportPunchesAsync(AppUser by, DateOnly from, DateOnly to, string path, CancellationToken ct = default) =>
        ExportAsync(by, "punches", from, to, path, async uow =>
        {
            var (fromUtc, toUtc) = SiteTime.CoveringUtcRange(From(from), From(to));
            var punches = (await uow.ListPunchesForExportAsync(fromUtc, toUtc, ct))
                .Where(r => InPeriod(r.Punch.OccurredAtLocal.DateTime, from, to));
            var bytes = Csv.Build(
                ["punch_id", "employee_id", "legacy_id", "employee", "direction", "site_time", "utc_offset_minutes",
                 "occurred_utc", "recorded_utc", "source", "counts", "superseded_by_correction", "created_by_correction"],
                punches.Select(r => (IReadOnlyList<object?>)
                [
                    r.Punch.Id, r.Punch.EmployeeId, r.LegacyId, r.EmployeeName, Name(r.Punch.Direction),
                    Csv.LocalTime(r.Punch.OccurredAtLocal.DateTime), r.Punch.UtcOffsetMinutes,
                    Csv.UtcTime(r.Punch.OccurredAtUtc), Csv.UtcTime(r.Punch.RecordedAtUtc), Name(r.Punch.Source),
                    r.SupersededByCorrectionId is null ? "yes" : "no", r.SupersededByCorrectionId, r.CreatedByCorrectionId,
                ]),
                out var rows);
            return (bytes, rows);
        }, ct);

    /// <summary>Every correction made on a site date in the period, with what it changed and who made it.</summary>
    public Task<ExportResult> ExportCorrectionsAsync(AppUser by, DateOnly from, DateOnly to, string path, CancellationToken ct = default) =>
        ExportAsync(by, "corrections", from, to, path, async uow =>
        {
            var zone = SiteTime.ResolveZone(await uow.GetSettingAsync(SiteSettingKeys.TimeZoneId, ct), TimeZoneInfo.Local);
            var (fromUtc, toUtc) = SiteTime.CoveringUtcRange(From(from), From(to));
            var corrections = (await uow.ListCorrectionsForExportAsync(fromUtc, toUtc, ct))
                .Select(c => (Row: c, Made: TimeZoneInfo.ConvertTime(c.CreatedUtc, zone).DateTime))
                .Where(c => InPeriod(c.Made, from, to));
            var bytes = Csv.Build(
                ["correction_id", "made_utc", "made_site_time", "action", "employee_id", "legacy_id", "employee",
                 "target_punch_id", "old_direction", "old_site_time", "new_direction", "new_site_time", "reason",
                 "by_username", "by_name"],
                corrections.Select(c => (IReadOnlyList<object?>)
                [
                    c.Row.Id, Csv.UtcTime(c.Row.CreatedUtc), Csv.LocalTime(c.Made), c.Row.Action.ToString().ToLowerInvariant(),
                    c.Row.EmployeeId, c.Row.LegacyId, c.Row.EmployeeName, c.Row.Target?.Id,
                    c.Row.Target is { } t ? Name(t.Direction) : null,
                    c.Row.Target is { } o ? Csv.LocalTime(o.OccurredAtLocal.DateTime) : null,
                    c.Row.NewDirection is { } d ? Name(d) : null,
                    c.Row.NewOccurredUtc is { } at && c.Row.NewUtcOffsetMinutes is { } offset
                        ? Csv.LocalTime(at.ToOffset(TimeSpan.FromMinutes(offset)).DateTime) : null,
                    c.Row.Reason, c.Row.ActorUsername, c.Row.ActorName,
                ]),
                out var rows);
            return (bytes, rows);
        }, ct);

    private static async Task<PayReport> BuildPayReportAsync(IPunchClockUnitOfWork uow, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (fromUtc, toUtc) = SiteTime.CoveringUtcRange(From(from), From(to));
        var shifts = await uow.ListShiftsAsync(fromUtc, toUtc, ct);
        var employees = (await uow.ListEmployeesAsync(activeOnly: false, ct)).ToDictionary(e => e.Id);
        var periodEnd = From(to.AddDays(1));

        var lines = new List<PayReportLine>();
        var notCounted = new List<UncountedShift>();
        foreach (var group in shifts.GroupBy(s => s.EmployeeId))
        {
            var employee = employees[group.Key];
            var (count, minutes, elapsed, skipped) = (0, 0L, 0m, 0);
            foreach (var shift in group)
            {
                var inLocal = shift.In.OccurredAtLocal.DateTime;
                if (!InPeriod(inLocal, from, to))
                {
                    continue;
                }

                var outLocal = shift.Out?.OccurredAtLocal.DateTime;
                if (shift.Out is null || outLocal >= periodEnd)
                {
                    skipped++;
                    notCounted.Add(new UncountedShift(employee.Id, employee.DisplayName, inLocal, outLocal, shift.Out is null
                        ? "No punch-out: add it on the Corrections tab."
                        : "Ends after the period, so the old report's rule does not count it in either period."));
                    continue;
                }

                count++;
                minutes += PayRules.AccessDateDiffMinutes(inLocal, outLocal!.Value);
                elapsed += (decimal)(shift.Out.OccurredAtUtc - shift.In.OccurredAtUtc).TotalMinutes;
            }

            if (count > 0 || skipped > 0)
            {
                lines.Add(new PayReportLine(employee.Id, employee.LegacyId, employee.DisplayName, count, minutes, elapsed, skipped));
            }
        }

        return new PayReport(
            from,
            to,
            lines.OrderBy(l => employees[l.EmployeeId].LastName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(l => employees[l.EmployeeId].FirstName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(l => l.EmployeeId)
                .ToList(),
            notCounted.OrderBy(s => s.InLocal).ToList());
    }

    private async Task<ExportResult> ExportAsync(
        AppUser by, string kind, DateOnly from, DateOnly to, string path,
        Func<IPunchClockUnitOfWork, Task<(byte[] Bytes, int Rows)>> build, CancellationToken ct)
    {
        EnsureAllowed(by, from, to);
        path = Path.GetFullPath(path);
        await using var uow = await store.BeginAsync(ct);
        var (bytes, rows) = await build(uow);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        // Written in full beside the target, then moved into place, so a reader never sees half a file.
        var temporary = path + ".partial";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }

        File.Move(temporary, path, overwrite: true);
        try
        {
            uow.ActAs(AuditActor.ForUser(by.Id));
            await uow.RecordEventAsync(AuditEvent.ReportExport, JsonSerializer.Serialize(new
            {
                kind,
                from = from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                to = to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                rows,
                file = path,
                bytes = bytes.LongLength,
                sha256,
            }), ct);
            await uow.CommitAsync(ct);
        }
        catch
        {
            // An export the log does not record must not exist.
            File.Delete(path);
            throw;
        }

        return new ExportResult(path, rows, bytes.LongLength, sha256);
    }

    private static void EnsureAllowed(AppUser by, DateOnly from, DateOnly to)
    {
        if (by is not { Role: UserRole.Admin or UserRole.Manager, IsActive: true })
        {
            throw new UnauthorizedAccessException("Only admins and managers can run reports.");
        }

        if (to < from)
        {
            throw new ArgumentException("The period ends before it starts.", nameof(to));
        }
    }

    private static DateTime From(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);

    private static bool InPeriod(DateTime local, DateOnly from, DateOnly to) =>
        local >= From(from) && local < From(to.AddDays(1));

    private static string Name(PunchDirection direction) => direction == PunchDirection.In ? "IN" : "OUT";

    private static string Name(PunchSource source) => source switch
    {
        PunchSource.Kiosk => "kiosk",
        PunchSource.Correction => "correction",
        _ => "legacy_import",
    };
}
