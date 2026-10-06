using System.Globalization;

namespace PunchClock.Migration.Export;

/// <summary>
/// Recomputes the aggregates Access reported at export time (non-NULL counts,
/// sums of whole-number columns, date bounds) from the parsed rows. A mismatch
/// means the CSV and the database it came from disagree, so nothing is imported.
/// Exports without control totals (the older exporter) skip this check.
/// </summary>
internal static class ControlTotalsCheck
{
    public static void Verify(
        ManifestTable employeeTable,
        IReadOnlyList<LegacyEmployee> employees,
        ManifestTable shiftTable,
        IReadOnlyList<LegacyShift> shifts)
    {
        if (employeeTable.ControlTotals is { } et)
        {
            Number(employeeTable, et, "EmployeeID", employees.Select(e => (long?)e.EmployeeId));
            Text(employeeTable, et, "FirstName", employees.Select(e => e.FirstName));
            Text(employeeTable, et, "LastName", employees.Select(e => e.LastName));
            Number(employeeTable, et, "PinCode", employees.Select(e => e.PinCode is null ? (long?)null : long.Parse(e.PinCode, CultureInfo.InvariantCulture)));
            // Raw values, so the sum is Access's own (Yes is -1); the exporter sends no sum for Yes/No columns.
            Number(employeeTable, et, "IsActive", employees.Select(e => e.IsActiveRaw));
        }

        if (shiftTable.ControlTotals is { } st)
        {
            Number(shiftTable, st, "ShiftID", shifts.Select(s => (long?)s.ShiftId));
            Number(shiftTable, st, "EmployeeID", shifts.Select(s => s.EmployeeId));
            Date(shiftTable, st, "TimeIn", shifts.Select(s => s.TimeIn));
            Date(shiftTable, st, "TimeOut", shifts.Select(s => s.TimeOut));
        }
    }

    static ColumnTotals? Get(ManifestTable table, IReadOnlyDictionary<string, ColumnTotals> totals, string column) =>
        totals.TryGetValue(column, out var t) ? t : null;

    static void Count(ManifestTable table, string column, ColumnTotals t, long actual)
    {
        if (t.NonNull != actual)
            Fail(table, column, $"{actual} non-empty values, Access counted {t.NonNull}");
    }

    static void Text(ManifestTable table, IReadOnlyDictionary<string, ColumnTotals> totals, string column, IEnumerable<string?> values)
    {
        if (Get(table, totals, column) is { } t)
            Count(table, column, t, values.Count(v => v is not null));
    }

    static void Number(ManifestTable table, IReadOnlyDictionary<string, ColumnTotals> totals, string column, IEnumerable<long?> values)
    {
        if (Get(table, totals, column) is not { } t)
            return;
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        Count(table, column, t, present.Count);
        if (t.Sum is null)
            return;
        if (!decimal.TryParse(t.Sum, NumberStyles.Float, CultureInfo.InvariantCulture, out var expected))
            Fail(table, column, $"manifest sum '{t.Sum}' is not a number");
        var actual = present.Aggregate(0m, (acc, v) => acc + v);
        if (actual != expected)
            Fail(table, column, $"sum is {actual}, Access computed {t.Sum}");
    }

    static void Date(ManifestTable table, IReadOnlyDictionary<string, ColumnTotals> totals, string column, IEnumerable<DateTime?> values)
    {
        if (Get(table, totals, column) is not { } t)
            return;
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        Count(table, column, t, present.Count);
        string? Iso(DateTime? d) => d?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var min = Iso(present.Count == 0 ? null : present.Min());
        var max = Iso(present.Count == 0 ? null : present.Max());
        if (t.Min != min || t.Max != max)
            Fail(table, column, $"range is {min}..{max}, Access reported {t.Min}..{t.Max}");
    }

    static void Fail(ManifestTable table, string column, string detail) =>
        throw new ExportFormatException($"{table.File}: control total for {column} does not match ({detail}). Export again.");
}
