using System.Globalization;
using PunchClock.Migration.Export;

namespace PunchClock.Migration.Analysis;

/// <summary>
/// Turns a verified legacy export into an import plan: every employee and
/// shift row with its legacy ID, times resolved to UTC, a disposition per
/// shift, and every anomaly flagged. No legacy value is repaired here.
/// </summary>
public static class ImportAnalyzer
{
    public static readonly TimeSpan LongShiftThreshold = TimeSpan.FromHours(16);

    /// <summary>Shifts from 0 to under 1 s are zero-length, as in the exporter's validator (PR #4).</summary>
    public static readonly TimeSpan ZeroLengthBound = TimeSpan.FromSeconds(1);

    /// <summary>
    /// NewStaffForm read DateTime.Now twice for its dummy row, so it can end a few ms after it
    /// starts, or a whole second later when the reads straddle a second and Access drops the
    /// fraction. An employee's first shift under 2 s is that dummy; no real shift is that short.
    /// </summary>
    public static readonly TimeSpan DummyBound = TimeSpan.FromSeconds(2);

    /// <param name="nowUtc">The import clock; times after it cannot be stored. Defaults to now.</param>
    public static ImportPlan Analyze(LegacyExport export, TimeZoneInfo timeZone, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var findings = new List<Finding>();

        void Add(FindingCode code, string table, long? id, long? employeeId, string detail) =>
            findings.Add(new Finding(code, FindingInfo.LevelOf(code), table, id, employeeId, detail));

        // Export-level notes.
        var hasIsActive = export.Manifest.Tables.Single(t => t.Name == "Employee").Columns.Contains("IsActive");
        if (!hasIsActive)
            Add(FindingCode.IsActiveColumnMissing, "Employee", null, null, "Every employee is imported as active.");
        foreach (var column in export.UnmappedColumns)
            Add(FindingCode.UnmappedColumn, column.Split('.')[0], null, null, $"{column} stays in the export folder but is not imported.");
        if (!export.SnapshotVerified)
            Add(FindingCode.SnapshotMissing, "Export", null, null, "Keep the backup of PunchClock.accdb; it is the evidence for pre-migration data.");

        // The old app compared PINs as numbers, so "0123" was stored and matched as 123.
        var longestPin = export.Employees.Max(e => e.PinCode?.Length) ?? 0;

        // Employees, legacy IDs kept.
        var employees = new List<PlannedEmployee>();
        var byLegacyId = new Dictionary<long, PlannedEmployee>();
        foreach (var e in export.Employees)
        {
            var planned = new PlannedEmployee(
                e.EmployeeId,
                e.FirstName ?? "",
                e.LastName ?? "",
                e.PinCode,
                e.IsActive is null || e.IsActive != 0,
                e);
            employees.Add(planned);
            byLegacyId[e.EmployeeId] = planned;

            if (e.PinCode is null)
                Add(FindingCode.PinMissing, "Employee", e.EmployeeId, e.EmployeeId, "The import issues a temporary PIN, listed in the import report.");
            else if (e.PinCode.Length < 3)
                Add(FindingCode.PinLostLeadingZeros, "Employee", e.EmployeeId, e.EmployeeId,
                    $"Stored PIN has {e.PinCode.Length} digit(s). {LeadingZeroAdvice}");
            else if (e.PinCode.Length < longestPin)
                Add(FindingCode.PinMayHaveLostLeadingZeros, "Employee", e.EmployeeId, e.EmployeeId,
                    $"Stored PIN has {e.PinCode.Length} digits; the longest PIN in the old app has {longestPin}. {LeadingZeroAdvice}");
            if (string.IsNullOrWhiteSpace(e.FirstName) || string.IsNullOrWhiteSpace(e.LastName))
                Add(FindingCode.MissingName, "Employee", e.EmployeeId, e.EmployeeId, $"First '{e.FirstName}', last '{e.LastName}'. Imported as recorded.");
        }

        foreach (var group in employees
                     .GroupBy(e => (e.FirstName.Trim().ToUpperInvariant(), e.LastName.Trim().ToUpperInvariant()))
                     .Where(g => g.Count() > 1))
        {
            var ids = string.Join(", ", group.Select(g => g.LegacyEmployeeId));
            foreach (var e in group)
                Add(FindingCode.DuplicateName, "Employee", e.LegacyEmployeeId, e.LegacyEmployeeId, $"Employee IDs {ids} share this name.");
        }

        // Shifts: owner, times, then the reasons a shift is skipped.
        var drafts = new List<Draft>();
        void Flag(Draft d, FindingCode code, string detail)
        {
            d.Flags.Add(code);
            Add(code, "Shift", d.Source.ShiftId, d.Source.EmployeeId, detail);
        }

        foreach (var s in export.Shifts)
        {
            var d = new Draft(s, s.EmployeeId is { } id ? byLegacyId.GetValueOrDefault(id) : null);
            drafts.Add(d);

            if (s.EmployeeId is null)
                Flag(d, FindingCode.MissingEmployeeId, "Raw row kept.");
            else if (d.Employee is null)
                Flag(d, FindingCode.OrphanShift, $"No Employee row has ID {s.EmployeeId}. Raw row kept.");

            if (s.TimeIn is not { } tin)
            {
                Flag(d, FindingCode.MissingTimeIn, "Raw row kept.");
                continue;
            }

            if (s.TimeOut is { } tout && tout >= tin)
            {
                // The old app's NewStaffForm wrote one of these for every new employee.
                var isFirst = d.Employee is not null && !export.Shifts.Any(o => o.EmployeeId == s.EmployeeId && o.ShiftId < s.ShiftId);
                if (tout - tin < (isFirst ? DummyBound : ZeroLengthBound))
                    Flag(d, isFirst ? FindingCode.DummyShift : FindingCode.ZeroLengthShift,
                        $"{Fmt(tin)} to {Fmt(tout)}. Counts as 0 hours either way.");
            }

            if (d.Flags.Any(FindingInfo.Skips))
                continue;

            d.In = Resolve(tin, timeZone, c => Flag(d, c, $"Punch in {Fmt(tin)}."));
            if (s.TimeOut is { } t)
                d.Out = Resolve(t, timeZone, c => Flag(d, c, $"Punch out {Fmt(t)}."));
            // Usually a PC clock set wrong once. The database refuses punches after its own clock.
            if (d.In.Value.Utc > now || d.Out?.Utc > now)
                Flag(d, FindingCode.FutureTime, $"{Fmt(tin)} to {(s.TimeOut is { } f ? Fmt(f) : "(open)")} is after the import time.");
        }

        // Every shift row counts here, skipped ones too: the old app read state from the row, not its hours.
        var lastById = drafts
            .Where(d => d.Employee is not null)
            .GroupBy(d => d.Employee!)
            .ToDictionary(g => g.Key, g => g.MaxBy(d => d.Source.ShiftId)!);

        foreach (var group in drafts.Where(d => d.In is not null).GroupBy(d => d.Employee!))
        {
            var byId = group.OrderBy(d => d.Source.ShiftId).ToList();
            var byTime = group.OrderBy(d => d.In!.Value.Local).ThenBy(d => d.Source.ShiftId).ToList();

            foreach (var d in byId.Where(d => d.Out is not null))
            {
                var tin = d.In!.Value;
                var tout = d.Out!.Value;
                var length = tout.Local - tin.Local;
                if (length < TimeSpan.Zero)
                    Flag(d, FindingCode.NegativeShift, $"{Fmt(tin.Local)} to {Fmt(tout.Local)} ({Hours(length)} h). The old report subtracts these hours.");
                else if (length > LongShiftThreshold)
                    Flag(d, FindingCode.LongShift, $"{Fmt(tin.Local)} to {Fmt(tout.Local)} ({Hours(length)} h).");

                if (tin.UtcOffsetMinutes != tout.UtcOffsetMinutes)
                    Flag(d, FindingCode.CrossesDstChange, $"Old report counts {Hours(length)} h, actual elapsed time is {Hours(tout.Utc - tin.Utc)} h.");
            }

            // The old app took punch state from the employee's highest ShiftID, so only
            // that shift is a live punch-in; any other open shift is a missed punch-out.
            var last = lastById[group.Key];
            foreach (var d in byTime.Where(d => d.Out is null))
            {
                if (ReferenceEquals(d, last))
                    Flag(d, FindingCode.OpenShiftCurrent, $"Punched in at {Fmt(d.In!.Value.Local)}. The old app shows this employee punched in.");
                else
                    Flag(d, FindingCode.OpenShiftStale, $"Punched in at {Fmt(d.In!.Value.Local)} and never out.");
            }

            // Compare each shift with the one that reaches latest so far, so a long
            // shift is caught overlapping every shift nested inside it.
            Draft? reach = null;
            foreach (var cur in byTime)
            {
                if (reach?.Out?.Local is { } end && end > cur.In!.Value.Local)
                    Flag(cur, FindingCode.OverlappingShift, $"Starts {Fmt(cur.In.Value.Local)}, before shift {reach.Source.ShiftId} ends at {Fmt(end)}.");
                if (cur.Out?.Local is { } o && o > cur.In!.Value.Local && (reach is null || o > reach.Out!.Value.Local))
                    reach = cur;
            }

            // The old app took punch state from the highest ShiftID, so an ID order
            // that disagrees with time order means a row was added by hand in Access.
            Draft? maxIn = null;
            foreach (var d in byId)
            {
                if (maxIn is not null && d.In!.Value.Local < maxIn.In!.Value.Local)
                    Flag(d, FindingCode.OutOfOrderShiftId, $"Starts {Fmt(d.In.Value.Local)}, before shift {maxIn.Source.ShiftId} ({Fmt(maxIn.In.Value.Local)}), which has a lower ID.");
                if (maxIn is null || d.In!.Value.Local > maxIn.In!.Value.Local)
                    maxIn = d;
            }
        }

        // The new app reads state from the latest punch by time, the old app from the
        // highest ShiftID. Where the two disagree the kiosk would flip someone's state.
        foreach (var (employee, last) in lastById)
        {
            var oldIn = last.Source.TimeOut is null;
            var latest = drafts
                .Where(d => d.Employee == employee && d.In is not null && !d.Flags.Any(FindingInfo.Skips))
                .OrderBy(d => d.Source.ShiftId)
                .SelectMany(d => d.Out is { } o
                    ? new[] { (At: d.In!.Value, In: true, Shift: d), (At: o, In: false, Shift: d) }
                    : [(At: d.In!.Value, In: true, Shift: d)])
                .Select((p, order) => (p.At, p.In, p.Shift, Order: order))
                .OrderBy(p => p.At.Utc)
                .ThenBy(p => p.Order)
                .LastOrDefault();
            var newIn = latest.Shift is not null && latest.In;
            if (oldIn == newIn)
                continue;
            var why = latest.Shift is null
                ? "it has no punches for this employee"
                : $"its latest punch is the punch {(latest.In ? "in" : "out")} of shift {latest.Shift.Source.ShiftId} at {Fmt(latest.At.Local)}";
            Add(FindingCode.PunchStateDiffers, "Employee", employee.LegacyEmployeeId, employee.LegacyEmployeeId,
                $"The old app shows this employee punched {(oldIn ? "in" : "out")} (shift {last.Source.ShiftId} has the highest ID). " +
                $"The new app will show punched {(newIn ? "in" : "out")}, because {why}. Correct the punches before this employee next punches.");
        }

        var shifts = drafts.Select(d => new PlannedShift(
                d.Source,
                d.Employee,
                d.In,
                d.Out,
                d.Flags,
                d.Flags.Any(FindingInfo.Skips) ? Disposition.Skipped
                : d.Flags.Any(c => FindingInfo.SchemaCode(c) is not null) ? Disposition.ImportedFlagged
                : Disposition.Imported))
            .ToList();

        foreach (var e in employees.Where(e => !shifts.Any(s => s.Employee == e && s.Disposition != Disposition.Skipped)))
            Add(FindingCode.NoShifts, "Employee", e.LegacyEmployeeId, e.LegacyEmployeeId, "No punches to import for this employee.");

        return new ImportPlan
        {
            Export = export,
            TimeZone = timeZone,
            Employees = employees,
            Shifts = shifts,
            Findings = findings
                .OrderBy(f => f.Level == FindingLevel.Review ? 0 : 1)
                .ThenBy(f => f.Code)
                .ThenBy(f => f.LegacyId)
                .ToList(),
            Totals = ReconciliationTotals.Compute(employees, shifts),
        };
    }

    /// <summary>
    /// Converts a legacy wall-clock time to UTC. In the repeated hour when
    /// clocks go back, the earlier (daylight) instant is used; in the skipped
    /// hour when clocks go forward, the offset in force before the gap is used,
    /// which moves the time forward by the DST step, and the offset stored is
    /// the one in force at that resulting instant. Both are flagged because
    /// the true instant cannot be known.
    /// </summary>
    public static ResolvedTime Resolve(DateTime local, TimeZoneInfo zone, Action<FindingCode>? flag = null)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (zone.IsInvalidTime(local))
        {
            var utc = DateTime.SpecifyKind(local - zone.GetUtcOffset(local.AddHours(-6)), DateTimeKind.Utc);
            flag?.Invoke(FindingCode.DstNonexistentTime);
            return new ResolvedTime(local, utc, (int)zone.GetUtcOffset(utc).TotalMinutes);
        }
        else if (zone.IsAmbiguousTime(local))
        {
            offset = zone.GetAmbiguousTimeOffsets(local).Max();
            flag?.Invoke(FindingCode.DstAmbiguousTime);
        }
        else
        {
            offset = zone.GetUtcOffset(local);
        }

        return new ResolvedTime(local, DateTime.SpecifyKind(local - offset, DateTimeKind.Utc), (int)offset.TotalMinutes);
    }

    const string LeadingZeroAdvice =
        "If this employee's PIN started with 0, they must type it without the leading zero(s) at their first punch " +
        "(123 for 0123), then choose a new PIN. Warn them, or reset their PIN on the Employees tab before cutover.";

    static string Fmt(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    static string Hours(TimeSpan t) => t.TotalHours.ToString("0.00", CultureInfo.InvariantCulture);

    sealed class Draft(LegacyShift source, PlannedEmployee? employee)
    {
        public LegacyShift Source { get; } = source;
        public PlannedEmployee? Employee { get; } = employee;
        public List<FindingCode> Flags { get; } = [];
        public ResolvedTime? In { get; set; }
        public ResolvedTime? Out { get; set; }
    }
}
