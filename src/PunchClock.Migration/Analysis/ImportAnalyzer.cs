using PunchClock.Migration.Export;

namespace PunchClock.Migration.Analysis;

/// <summary>
/// Turns a verified legacy export into an import plan: every employee and
/// shift row, legacy IDs kept, times resolved to UTC, and every anomaly
/// flagged. Nothing is dropped, merged or repaired here.
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
            Add(FindingCode.UnmappedColumn, column.Split('.')[0], null, null, $"{column} is kept in the export folder but not imported.");
        if (!export.SnapshotVerified)
            Add(FindingCode.SnapshotMissing, "Export", null, null, "Keep the original PunchClock.accdb backup; it is the evidence for pre-migration data.");

        // Employees, legacy IDs kept.
        var employees = new List<PlannedEmployee>();
        var byLegacyId = new Dictionary<long, PlannedEmployee>();
        foreach (var e in export.Employees)
        {
            var planned = new PlannedEmployee(
                e.EmployeeId,
                e.FirstName?.Trim() ?? "",
                e.LastName?.Trim() ?? "",
                e.PinCode,
                e.IsActive is null ? true : e.IsActive != 0,
                IsPlaceholder: false,
                e);
            employees.Add(planned);
            byLegacyId[e.EmployeeId] = planned;

            if (e.PinCode is null)
                Add(FindingCode.PinMissing, "Employee", e.EmployeeId, e.EmployeeId, "Set a PIN in the new app before this employee can punch.");
            else if (e.PinCode.Length < 3)
                Add(FindingCode.PinLostLeadingZeros, "Employee", e.EmployeeId, e.EmployeeId, $"Stored PIN has {e.PinCode.Length} digit(s).");
            if (string.IsNullOrWhiteSpace(e.FirstName) || string.IsNullOrWhiteSpace(e.LastName))
                Add(FindingCode.MissingName, "Employee", e.EmployeeId, e.EmployeeId, $"First '{e.FirstName}', last '{e.LastName}'.");
        }

        foreach (var group in employees
                     .GroupBy(e => (e.FirstName.ToUpperInvariant(), e.LastName.ToUpperInvariant()))
                     .Where(g => g.Count() > 1))
        {
            var ids = string.Join(", ", group.Select(g => g.LegacyEmployeeId));
            foreach (var e in group)
                Add(FindingCode.DuplicateName, "Employee", e.LegacyEmployeeId, e.LegacyEmployeeId, $"Employee IDs {ids} share the name {ReconciliationTotals.DisplayName(e)}.");
        }

        // Orphan shifts get one inactive placeholder per missing employee ID, so
        // the rows are kept and stay visible instead of being discarded.
        PlannedEmployee? noIdPlaceholder = null;
        PlannedEmployee OwnerOf(LegacyShift s)
        {
            if (s.EmployeeId is not { } id)
            {
                if (noIdPlaceholder is null)
                {
                    noIdPlaceholder = new PlannedEmployee(null, "Unknown", "(no employee ID)", null, false, true, null);
                    employees.Add(noIdPlaceholder);
                    Add(FindingCode.PlaceholderEmployee, "Employee", null, null, "Holds shifts that have no employee ID.");
                }
                return noIdPlaceholder;
            }

            if (byLegacyId.TryGetValue(id, out var existing))
                return existing;

            var placeholder = new PlannedEmployee(id, "Unknown", $"(legacy #{id})", null, false, true, null);
            employees.Add(placeholder);
            byLegacyId[id] = placeholder;
            Add(FindingCode.PlaceholderEmployee, "Employee", null, id, $"No Employee row has ID {id}; imported inactive, without a PIN.");
            return placeholder;
        }

        // Shifts: resolve times, then flag.
        var flags = new Dictionary<long, List<FindingCode>>();
        void Flag(PlannedShiftDraft d, FindingCode code, string detail)
        {
            flags[d.Source.ShiftId].Add(code);
            Add(code, "Shift", d.Source.ShiftId, d.Source.EmployeeId, detail);
        }

        var drafts = new List<PlannedShiftDraft>();
        foreach (var s in export.Shifts)
        {
            flags[s.ShiftId] = [];
            var owner = OwnerOf(s);
            var draft = new PlannedShiftDraft(s, owner);
            drafts.Add(draft);

            if (owner.IsPlaceholder)
            {
                if (s.EmployeeId is null)
                    Flag(draft, FindingCode.MissingEmployeeId, "Assigned to the 'Unknown (no employee ID)' placeholder.");
                else
                    Flag(draft, FindingCode.OrphanShift, $"Employee ID {s.EmployeeId} does not exist; assigned to its placeholder.");
            }

            if (s.TimeIn is { } tin)
                draft.In = Resolve(tin, timeZone, c => Flag(draft, c, $"Punch in {Fmt(tin)}."));
            else
                Flag(draft, FindingCode.MissingTimeIn, "Kept as a legacy record; not counted in hours.");

            if (s.TimeOut is { } tout)
                draft.Out = Resolve(tout, timeZone, c => Flag(draft, c, $"Punch out {Fmt(tout)}."));
        }

        foreach (var group in drafts.GroupBy(d => d.Employee))
        {
            var byId = group.OrderBy(d => d.Source.ShiftId).ToList();
            var withIn = group.Where(d => d.In is not null)
                .OrderBy(d => d.In!.Value.Local).ThenBy(d => d.Source.ShiftId).ToList();
            var firstById = byId[0];

            foreach (var d in byId)
            {
                var s = d.Source;
                if (s.TimeIn is { } tin && s.TimeOut is { } tout)
                {
                    var length = tout - tin;
                    if (length == TimeSpan.Zero)
                    {
                        if (ReferenceEquals(d, firstById) && !d.Employee.IsPlaceholder)
                            Flag(d, FindingCode.DummyShift, "Counts as 0 hours.");
                        else
                            Flag(d, FindingCode.ZeroLengthShift, $"At {Fmt(tin)}. Counts as 0 hours.");
                    }
                    else if (length < TimeSpan.Zero)
                        Flag(d, FindingCode.NegativeShift, $"{Fmt(tin)} to {Fmt(tout)} ({Hours(length)} h). The old report subtracts these hours.");
                    else if (length > LongShiftThreshold)
                        Flag(d, FindingCode.LongShift, $"{Fmt(tin)} to {Fmt(tout)} ({Hours(length)} h).");

                    if (d.In is { } i && d.Out is { } o && i.UtcOffsetMinutes != o.UtcOffsetMinutes)
                    {
                        var delta = (o.Utc - i.Utc) - length;
                        Flag(d, FindingCode.CrossesDstChange, $"Old report counts {Hours(length)} h, actual elapsed time is {Hours(length + delta)} h.");
                    }
                }
            }

            // Open shifts: the employee's latest shift by time is a live punch-in;
            // any other open shift is a missed punch-out.
            var latest = withIn.LastOrDefault();
            foreach (var d in withIn.Where(d => d.Out is null))
            {
                if (ReferenceEquals(d, latest))
                    Flag(d, FindingCode.OpenShiftCurrent, $"Punched in at {Fmt(d.Source.TimeIn!.Value)}. The new app starts with this employee punched in.");
                else
                    Flag(d, FindingCode.OpenShiftStale, $"Punched in at {Fmt(d.Source.TimeIn!.Value)} and never out.");
            }

            // Overlap by time. An open shift extends to the next shift's start.
            for (var k = 1; k < withIn.Count; k++)
            {
                var prev = withIn[k - 1];
                var cur = withIn[k];
                var prevEnd = prev.Out?.Local;
                if (prevEnd is { } end && end > cur.In!.Value.Local && end > prev.In!.Value.Local)
                    Flag(cur, FindingCode.OverlappingShift, $"Starts {Fmt(cur.In.Value.Local)}, shift {prev.Source.ShiftId} ends {Fmt(end)}.");
            }

            // The old app derived punch state from the highest ShiftID, so a row
            // whose ID order disagrees with its time order was inserted by hand.
            DateTime? maxIn = null;
            long maxInShift = 0;
            foreach (var d in byId.Where(d => d.In is not null))
            {
                var t = d.In!.Value.Local;
                if (maxIn is { } m && t < m)
                    Flag(d, FindingCode.OutOfOrderShiftId, $"Shift {d.Source.ShiftId} starts {Fmt(t)}, before shift {maxInShift} ({Fmt(m)}) which has a lower ID.");
                if (maxIn is null || t > maxIn)
                {
                    maxIn = t;
                    maxInShift = d.Source.ShiftId;
                }
            }
        }

        var shifts = drafts
            .Select(d => new PlannedShift(d.Source, d.Employee, d.In, d.Out, flags[d.Source.ShiftId]))
            .ToList();

        foreach (var e in employees.Where(e => !e.IsPlaceholder))
        {
            if (!shifts.Any(s => ReferenceEquals(s.Employee, e)))
                Add(FindingCode.NoShifts, "Employee", e.LegacyEmployeeId, e.LegacyEmployeeId, "Nothing to import for this employee besides the record itself.");
        }

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
            Totals = ReconciliationTotals.Compute(export.Employees.Count, employees, shifts),
        };
    }

    /// <summary>
    /// Converts a legacy wall-clock time to UTC. In the repeated hour when
    /// clocks go back, the earlier (daylight) instant is used; in the skipped
    /// hour when clocks go forward, the offset in force just before the gap is
    /// used. Both cases are flagged because the true instant is unknowable.
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

    static string Fmt(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss");

    static string Hours(TimeSpan t) => t.TotalHours.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    sealed class PlannedShiftDraft(LegacyShift source, PlannedEmployee employee)
    {
        public LegacyShift Source { get; } = source;
        public PlannedEmployee Employee { get; } = employee;
        public ResolvedTime? In { get; set; }
        public ResolvedTime? Out { get; set; }
    }
}
