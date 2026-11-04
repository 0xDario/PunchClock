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
                    [l.EmployeeId, l.LegacyId, l.Name, l.Shifts, l.Minutes, Hours(l.Hours), Hours(l.ElapsedHours), l.NotCounted]),
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

    /// <summary>
    /// Every shift in the period, one line each, as the old Access report printed them, with the
    /// same totals as <see cref="PayReportAsync"/>. Shifts the pay report leaves out, voided punches
    /// and punch-outs with no punch-in are listed too, and each punch a manager added or changed
    /// carries who did it, when and why, so the printout can be checked against what happened.
    /// </summary>
    public async Task<ShiftDetailReport> ShiftDetailReportAsync(AppUser by, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        EnsureAllowed(by, from, to);
        await using var uow = await store.BeginAsync(ct);
        return await BuildShiftDetailAsync(uow, by, from, to, ct);
    }

    /// <summary>
    /// <see cref="ShiftDetailReportAsync"/> as a PDF to print, recorded in the audit log like the
    /// CSV exports. The printout names the audit log's newest entry, so a paper copy also anchors the log.
    /// </summary>
    public Task<ExportResult> ExportShiftDetailPdfAsync(AppUser by, DateOnly from, DateOnly to, string path, CancellationToken ct = default) =>
        ExportAsync(by, "shift_detail", ".pdf", from, to, path, async uow =>
        {
            var report = await BuildShiftDetailAsync(uow, by, from, to, ct);
            return (ShiftDetailPdf.Render(report), report.Employees.Sum(e => e.Rows.Count));
        }, ct);

    private static async Task<ShiftDetailReport> BuildShiftDetailAsync(
        IPunchClockUnitOfWork uow, AppUser by, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var zone = SiteTime.ResolveZone(await uow.GetSettingAsync(SiteSettingKeys.TimeZoneId, ct), TimeZoneInfo.Local);
        var (fromUtc, toUtc) = SiteTime.CoveringUtcRange(From(from), From(to));
        var shifts = (await uow.ListShiftsAsync(fromUtc, toUtc, ct)).Where(s => InPeriod(s.In.OccurredAtLocal.DateTime, from, to)).ToList();
        var punches = (await uow.ListPunchesForExportAsync(fromUtc, toUtc, ct))
            .Where(r => InPeriod(r.Punch.OccurredAtLocal.DateTime, from, to))
            .ToList();
        var missingIn = (await uow.ListPunchesMissingInAsync(fromUtc, toUtc, ct)).ToHashSet();

        // A shift that starts in the period can end after it; its punch-out's history is needed too.
        var historyTo = shifts.Select(s => s.Out?.OccurredAtUtc.AddMilliseconds(1)).Append(toUtc).Max()!.Value;
        var history = await uow.ListCorrectionHistoryAsync(fromUtc, historyTo, ct);
        var corrections = history.ToDictionary(c => c.Id);
        var madeBy = history.Where(c => c.CreatedPunchId is not null).ToDictionary(c => c.CreatedPunchId!.Value);
        var replacedBy = history.Where(c => c.Target is not null).ToDictionary(c => c.Target!.Id);

        List<string> Notes(Punch punch)
        {
            // Walk back from the punch to the first version of it, then print oldest first.
            var chain = new List<CorrectionHistoryRow>();
            for (var id = punch.Id; madeBy.TryGetValue(id, out var c);)
            {
                chain.Add(c);
                if (c.Target is not { } target)
                {
                    break;
                }

                id = target.Id;
            }

            chain.Reverse();
            return chain.Select(c => Describe(c, zone)).ToList();
        }

        // No version of the punch is left in the period: voided, or changed to a time outside it.
        bool LeavesPeriod(CorrectionHistoryRow c) => c.Action == CorrectionAction.Void
            || !InPeriod(c.NewOccurredUtc!.Value.ToOffset(TimeSpan.FromMinutes(c.NewUtcOffsetMinutes!.Value)).DateTime, from, to);

        var rows = new Dictionary<long, List<ShiftDetailRow>>();
        var paid = new Dictionary<long, (int Shifts, long Minutes)>();
        void Add(long employeeId, ShiftDetailRow row)
        {
            if (!rows.TryGetValue(employeeId, out var list))
            {
                rows[employeeId] = list = [];
            }

            list.Add(row);
        }

        foreach (var shift in shifts)
        {
            var (_, minutes, reason) = Classify(shift, from, to);
            var notes = Notes(shift.In);
            if (shift.Out is { } punchOut)
            {
                notes.AddRange(Notes(punchOut));
            }

            if (reason is not null)
            {
                notes.Add("Not counted. " + reason);
            }
            else
            {
                var (count, total) = paid.GetValueOrDefault(shift.EmployeeId);
                paid[shift.EmployeeId] = (count + 1, total + minutes!.Value);
            }

            Add(shift.EmployeeId, new ShiftDetailRow(
                reason is null ? ShiftDetailKind.Counted : ShiftDetailKind.NotCounted,
                shift.In.OccurredAtLocal.DateTime,
                shift.Out?.OccurredAtLocal.DateTime,
                minutes,
                shift.In.Source == PunchSource.Correction,
                shift.Out?.Source == PunchSource.Correction,
                notes));
        }

        foreach (var row in punches)
        {
            var punch = row.Punch;
            if (row.SupersededByCorrectionId is { } supersededBy)
            {
                // A replaced punch whose latest version is in the period is shown with that version.
                // Otherwise the chain gets one row, at its last version in the period, saying what
                // became of it: voided, or moved out of the period.
                var later = new List<CorrectionHistoryRow> { corrections[supersededBy] };
                while (later[^1].CreatedPunchId is { } made && replacedBy.TryGetValue(made, out var next))
                {
                    later.Add(next);
                }

                if (!LeavesPeriod(later[0]) || !LeavesPeriod(later[^1]))
                {
                    continue;
                }

                var notes = Notes(punch);
                notes.AddRange(later.Select(c => Describe(c, zone)));
                Add(punch.EmployeeId, Single(
                    later[^1].Action == CorrectionAction.Void ? ShiftDetailKind.Voided : ShiftDetailKind.Moved, punch, notes));
            }
            else if (missingIn.Contains(punch.Id))
            {
                var notes = Notes(punch);
                notes.Add("No punch-in before this punch-out: add the missing punch-in on the Corrections tab.");
                Add(punch.EmployeeId, Single(ShiftDetailKind.MissingIn, punch, notes));
            }
        }

        var employees = await uow.ListEmployeesAsync(activeOnly: false, ct);
        var head = await uow.GetAuditHeadAsync(ct);
        var now = TimeZoneInfo.ConvertTime(await uow.GetDatabaseUtcNowAsync(ct), zone);
        return new ShiftDetailReport(
            from,
            to,
            employees.Where(e => rows.ContainsKey(e.Id))
                .OrderBy(e => e.LastName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.FirstName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Id)
                .Select(e =>
                {
                    var (count, minutes) = paid.GetValueOrDefault(e.Id);
                    return new ShiftDetailEmployee(
                        e.Id, e.LegacyId, e.FirstName, e.LastName,
                        rows[e.Id].OrderBy(r => r.SortTime).ThenBy(r => r.Kind).ToList(), count, minutes);
                })
                .ToList(),
            now,
            $"{by.DisplayName} ({by.Username})",
            head.Seq,
            head.Hash);
    }

    private static ShiftDetailRow Single(ShiftDetailKind kind, Punch punch, IReadOnlyList<string> notes)
    {
        var at = punch.OccurredAtLocal.DateTime;
        var isIn = punch.Direction == PunchDirection.In;
        var changed = punch.Source == PunchSource.Correction;
        return new ShiftDetailRow(kind, isIn ? at : null, isIn ? null : at, null, isIn && changed, !isIn && changed, notes);
    }

    /// <summary>One correction as a printed note, e.g. "In changed from 7:50:41 AM by Ann Lee on 9/9/2026: ...".</summary>
    private static string Describe(CorrectionHistoryRow c, TimeZoneInfo zone)
    {
        var made = TimeZoneInfo.ConvertTime(c.CreatedUtc, zone).ToString(ShiftDetailFormat.Date, ShiftDetailFormat.Culture);
        var by = $"by {c.ActorName} on {made}: {c.Reason.Trim()}";
        switch (c.Action)
        {
            case CorrectionAction.Add:
                return $"{Label(c.NewDirection!.Value)} added {by}";
            case CorrectionAction.Void:
                return $"{Label(c.Target!.Direction)} at {ShiftDetailFormat.Time(c.Target.OccurredAtLocal.DateTime)} voided {by}";
            default:
                var old = c.Target!;
                var now = c.NewOccurredUtc!.Value.ToOffset(TimeSpan.FromMinutes(c.NewUtcOffsetMinutes!.Value)).DateTime;
                var direction = old.Direction == c.NewDirection
                    ? Label(old.Direction)
                    : $"{Label(old.Direction)} (now {Label(c.NewDirection!.Value)})";
                return $"{direction} changed from {ShiftDetailFormat.Time(old.OccurredAtLocal.DateTime)} to {ShiftDetailFormat.Time(now)} {by}";
        }
    }

    private static string Label(PunchDirection direction) => direction == PunchDirection.In ? "In" : "Out";

    private static async Task<PayReport> BuildPayReportAsync(IPunchClockUnitOfWork uow, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (fromUtc, toUtc) = SiteTime.CoveringUtcRange(From(from), From(to));
        var shifts = await uow.ListShiftsAsync(fromUtc, toUtc, ct);
        var employees = (await uow.ListEmployeesAsync(activeOnly: false, ct)).ToDictionary(e => e.Id);

        var lines = new List<PayReportLine>();
        var notCounted = new List<UncountedShift>();
        foreach (var group in shifts.GroupBy(s => s.EmployeeId))
        {
            var employee = employees[group.Key];
            var (count, minutes, elapsed, skipped) = (0, 0L, 0m, 0);
            foreach (var shift in group)
            {
                switch (Classify(shift, from, to))
                {
                    case { InPeriod: false }:
                        continue;
                    case { Minutes: { } paid }:
                        count++;
                        minutes += paid;
                        elapsed += (decimal)(shift.Out!.OccurredAtUtc - shift.In.OccurredAtUtc).TotalMinutes;
                        break;
                    case { NotCountedReason: { } reason }:
                        skipped++;
                        notCounted.Add(new UncountedShift(
                            employee.Id, employee.DisplayName, shift.In.OccurredAtLocal.DateTime, shift.Out?.OccurredAtLocal.DateTime, reason));
                        break;
                }
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

    /// <summary>
    /// The pay rule for one shift. Not in the period when its IN is outside it. Otherwise paid
    /// minutes when it also ends before the day after <paramref name="to"/>, or the reason it is not paid.
    /// </summary>
    private static (bool InPeriod, long? Minutes, string? NotCountedReason) Classify(ShiftRecord shift, DateOnly from, DateOnly to)
    {
        var inLocal = shift.In.OccurredAtLocal.DateTime;
        if (!InPeriod(inLocal, from, to))
        {
            return (false, null, null);
        }

        if (shift.Out is null)
        {
            return (true, null, "No punch-out: add it on the Corrections tab.");
        }

        var outLocal = shift.Out.OccurredAtLocal.DateTime;
        return outLocal >= From(to.AddDays(1))
            ? (true, null, "Ends after the period, so the old report's rule does not count it in either period.")
            : (true, PayRules.AccessDateDiffMinutes(inLocal, outLocal), null);
    }

    private Task<ExportResult> ExportAsync(
        AppUser by, string kind, DateOnly from, DateOnly to, string path,
        Func<IPunchClockUnitOfWork, Task<(byte[] Bytes, int Rows)>> build, CancellationToken ct) =>
        ExportAsync(by, kind, ".csv", from, to, path, build, ct);

    private async Task<ExportResult> ExportAsync(
        AppUser by, string kind, string extension, DateOnly from, DateOnly to, string path,
        Func<IPunchClockUnitOfWork, Task<(byte[] Bytes, int Rows)>> build, CancellationToken ct)
    {
        EnsureAllowed(by, from, to);
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
        {
            // Never replace a database, backup or any other file the export was not meant for.
            throw new ArgumentException($"This export is saved as a {extension} file.", nameof(path));
        }

        byte[] bytes;
        int rows;
        await using (var read = await store.BeginAsync(ct))
        {
            (bytes, rows) = await build(read);
        }

        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        // The file is written with no transaction open: a slow USB stick or network share must
        // not hold the write lock the kiosk needs. Written in full beside the target, then moved
        // into place, so a reader never sees half a file.
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
            await using var uow = await store.BeginAsync(ct);
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

    /// <summary>Always two decimals, so a payroll import sees one column format.</summary>
    private static string Hours(decimal hours) => hours.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

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
