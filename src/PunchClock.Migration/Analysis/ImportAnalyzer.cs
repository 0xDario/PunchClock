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

    public static ImportPlan Analyze(LegacyExport export, TimeZoneInfo timeZone)
    {
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
                Add(FindingCode.PinMissing, "Employee", e.EmployeeId, e.EmployeeId, "Set a PIN in the new app before this employee can punch.");
            else if (e.PinCode.Length < 3)
                Add(FindingCode.PinLostLeadingZeros, "Employee", e.EmployeeId, e.EmployeeId, $"Stored PIN has {e.PinCode.Length} digit(s).");
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

            if (s.TimeOut is { } tout && tout == tin)
            {
                // The old app's NewStaffForm wrote one of these for every new employee.
                var isFirst = d.Employee is not null && !export.Shifts.Any(o => o.EmployeeId == s.EmployeeId && o.ShiftId < s.ShiftId);
                Flag(d, isFirst ? FindingCode.DummyShift : FindingCode.ZeroLengthShift, $"At {Fmt(tin)}. Counts as 0 hours either way.");
            }

            if (d.Flags.Any(FindingInfo.Skips))
                continue;

            d.In = Resolve(tin, timeZone, c => Flag(d, c, $"Punch in {Fmt(tin)}."));
            if (s.TimeOut is { } t)
                d.Out = Resolve(t, timeZone, c => Flag(d, c, $"Punch out {Fmt(t)}."));
        }

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

            // The employee's latest shift by time is a live punch-in; any other
            // open shift is a punch-out that never happened.
            var latest = byTime[^1];
            foreach (var d in byTime.Where(d => d.Out is null))
            {
                if (ReferenceEquals(d, latest))
                    Flag(d, FindingCode.OpenShiftCurrent, $"Punched in at {Fmt(d.In!.Value.Local)}. The new app starts with this employee punched in.");
                else
                    Flag(d, FindingCode.OpenShiftStale, $"Punched in at {Fmt(d.In!.Value.Local)} and never out.");
            }

            for (var k = 1; k < byTime.Count; k++)
            {
                var prev = byTime[k - 1];
                var cur = byTime[k];
                if (prev.Out?.Local is { } end && end > cur.In!.Value.Local && end > prev.In!.Value.Local)
                    Flag(cur, FindingCode.OverlappingShift, $"Starts {Fmt(cur.In.Value.Local)}, before shift {prev.Source.ShiftId} ends at {Fmt(end)}.");
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
    /// which moves the time forward by the DST step. Both are flagged because
    /// the true instant cannot be known.
    /// </summary>
    public static ResolvedTime Resolve(DateTime local, TimeZoneInfo zone, Action<FindingCode>? flag = null)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (zone.IsInvalidTime(local))
        {
            offset = zone.GetUtcOffset(local.AddHours(-6));
            flag?.Invoke(FindingCode.DstNonexistentTime);
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
