using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace PunchClock.Migration.Export;

public sealed class ExportFormatException(string message) : Exception(message);

/// <summary>One Employee row exactly as the legacy exporter wrote it.</summary>
/// <param name="IsActive">1 or 0; null when the export predates the column or the value is NULL.</param>
/// <param name="IsActiveRaw">The stored value as an integer (Access True is -1), kept for the evidence table.</param>
public sealed record LegacyEmployee(
    long EmployeeId,
    string? FirstName,
    string? LastName,
    string? PinCode,
    int? IsActive,
    long? IsActiveRaw,
    int Line);

/// <summary>
/// One Shift row. Times are site-local wall clock with no zone, as the legacy
/// app wrote them with <c>DateTime.Now</c>.
/// </summary>
/// <param name="Raw">The four timestamp fields exactly as exported, kept for the evidence table.</param>
public sealed record LegacyShift(
    long ShiftId,
    long? EmployeeId,
    DateTime? TimeIn,
    DateTime? TimeOut,
    RawShiftTimes Raw,
    int Line);

public sealed record RawShiftTimes(string? TimeIn, string? TimeInOADate, string? TimeOut, string? TimeOutOADate);

/// <summary>Access-computed aggregates for one column, from an export query independent of the row dump.</summary>
public sealed record ColumnTotals(long NonNull, string? Sum, string? Min, string? Max);

public sealed record ManifestTable(
    string Name,
    string File,
    long RowCount,
    long? MinId,
    long? MaxId,
    IReadOnlyList<string> Columns,
    string Sha256,
    IReadOnlyDictionary<string, ColumnTotals>? ControlTotals);

public sealed record ExportManifest(
    string Tool,
    string? ExportedAtUtc,
    string? SourcePath,
    long? SourceSizeBytes,
    string SourceSha256,
    string? SourceSnapshot,
    string? SiteTimeZoneId,
    IReadOnlyList<ManifestTable> Tables,
    IReadOnlyList<string> Warnings,
    string ManifestSha256);

/// <summary>
/// A verified legacy export folder: every CSV matches the hash, row count and
/// ID range its manifest records, and every row parsed. Anything else throws
/// <see cref="ExportFormatException"/>, because a partial or altered export
/// must never reach the new database.
/// </summary>
public sealed class LegacyExport
{
    public required string Folder { get; init; }
    public required ExportManifest Manifest { get; init; }
    public required IReadOnlyList<LegacyEmployee> Employees { get; init; }
    public required IReadOnlyList<LegacyShift> Shifts { get; init; }

    /// <summary>True when the byte-exact .accdb snapshot is present and matches the manifest.</summary>
    public required bool SnapshotVerified { get; init; }

    /// <summary>Columns present in the CSVs that the importer does not map. Reported, never dropped silently.</summary>
    public required IReadOnlyList<string> UnmappedColumns { get; init; }

    static readonly string[] EmployeeRequired = ["EmployeeID", "FirstName", "LastName", "PinCode"];
    static readonly string[] EmployeeOptional = ["IsActive"];
    static readonly string[] ShiftRequired = ["ShiftID", "EmployeeID", "TimeIn", "TimeIn_OADate", "TimeOut", "TimeOut_OADate"];

    public static LegacyExport Load(string folder)
    {
        var manifestPath = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new ExportFormatException($"No manifest.json in {folder}. Point the importer at the folder the exporter created.");

        var manifestBytes = File.ReadAllBytes(manifestPath);
        var manifest = ParseManifest(manifestBytes);

        var employeeTable = Table(manifest, "Employee");
        var shiftTable = Table(manifest, "Shift");

        var unmapped = new List<string>();
        var employees = ReadTable(folder, employeeTable, EmployeeRequired, EmployeeOptional, unmapped, ParseEmployee);
        var shifts = ReadTable(folder, shiftTable, ShiftRequired, [], unmapped, ParseShift);

        CheckIds(employeeTable, employees.Select(e => (e.EmployeeId, e.Line)).ToList());
        CheckIds(shiftTable, shifts.Select(s => (s.ShiftId, s.Line)).ToList());
        ControlTotalsCheck.Verify(employeeTable, employees, shiftTable, shifts);

        var snapshotVerified = false;
        if (manifest.SourceSnapshot is { Length: > 0 } snap)
        {
            var snapPath = Path.Combine(folder, snap.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(snapPath))
            {
                if (Sha256File(snapPath) != manifest.SourceSha256)
                    throw new ExportFormatException($"{snap} does not match the source SHA-256 in manifest.json. The snapshot was altered after export.");
                snapshotVerified = true;
            }
        }

        return new LegacyExport
        {
            Folder = Path.GetFullPath(folder),
            Manifest = manifest,
            Employees = employees,
            Shifts = shifts,
            SnapshotVerified = snapshotVerified,
            UnmappedColumns = unmapped,
        };
    }

    static ExportManifest ParseManifest(byte[] bytes)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new ExportFormatException($"manifest.json is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var source = Req(root, "source");
            var tables = Req(root, "tables").EnumerateArray().Select(t => new ManifestTable(
                ReqString(t, "name"),
                ReqString(t, "file"),
                Req(t, "row_count").GetInt64(),
                OptLong(t, "min_id"),
                OptLong(t, "max_id"),
                Req(t, "columns").EnumerateArray().Select(c => c.GetString() ?? "").ToList(),
                ReqString(t, "sha256").ToLowerInvariant(),
                ParseControlTotals(t))).ToList();

            // The exporter keeps the folder of a failed run so the failure can be read,
            // but such an export must never be imported.
            var failures = Strings(root, "failures");
            if (failures.Count > 0)
                throw new ExportFormatException("The export itself failed its checks: " + string.Join("; ", failures) + " Export again.");

            string? zoneId = null;
            if (root.TryGetProperty("site_time_zone", out var zone) && zone.ValueKind == JsonValueKind.Object)
                zoneId = OptString(zone, "id");

            return new ExportManifest(
                ReqString(root, "tool"),
                OptString(root, "exported_at_utc"),
                OptString(source, "path"),
                OptLong(source, "size_bytes"),
                ReqString(source, "sha256").ToLowerInvariant(),
                OptString(source, "snapshot"),
                zoneId,
                tables,
                Strings(root, "warnings"),
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    static IReadOnlyDictionary<string, ColumnTotals>? ParseControlTotals(JsonElement table)
    {
        if (!table.TryGetProperty("control_totals", out var totals) || totals.ValueKind != JsonValueKind.Object)
            return null;
        return totals.EnumerateObject().ToDictionary(
            p => p.Name,
            p => new ColumnTotals(
                Req(p.Value, "non_null").GetInt64(),
                OptScalar(p.Value, "sum"),
                OptScalar(p.Value, "min"),
                OptScalar(p.Value, "max")));
    }

    static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.ToString()).ToList()
            : [];

    static string? OptScalar(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    static ManifestTable Table(ExportManifest m, string name) =>
        m.Tables.SingleOrDefault(t => t.Name == name)
        ?? throw new ExportFormatException($"manifest.json has no entry for table {name}.");

    static List<T> ReadTable<T>(
        string folder,
        ManifestTable table,
        string[] required,
        string[] optional,
        List<string> unmapped,
        Func<Row, T> parse)
    {
        var path = Path.Combine(folder, table.File);
        if (!File.Exists(path))
            throw new ExportFormatException($"{table.File} is listed in manifest.json but missing from the folder.");

        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (hash != table.Sha256)
            throw new ExportFormatException($"{table.File} SHA-256 is {hash}, manifest.json says {table.Sha256}. The file was changed after export; export again.");

        var rows = Csv.Parse(new System.Text.UTF8Encoding(false, true).GetString(bytes), table.File);
        if (rows.Count == 0)
            throw new ExportFormatException($"{table.File} has no header row.");

        var header = rows[0].Select(h => h ?? "").ToArray();
        if (!header.SequenceEqual(table.Columns))
            throw new ExportFormatException($"{table.File} header does not match the columns in manifest.json.");

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Length; i++)
        {
            if (!index.TryAdd(header[i], i))
                throw new ExportFormatException($"{table.File} has duplicate column {header[i]}.");
        }

        var missing = required.Where(c => !index.ContainsKey(c)).ToList();
        if (missing.Count > 0)
            throw new ExportFormatException($"{table.File} is missing column(s): {string.Join(", ", missing)}.");

        unmapped.AddRange(header
            .Where(h => !required.Contains(h) && !optional.Contains(h))
            .Select(h => $"{table.Name}.{h}"));

        var result = new List<T>(rows.Count - 1);
        for (var r = 1; r < rows.Count; r++)
        {
            if (rows[r].Length != header.Length)
                throw new ExportFormatException($"{table.File} line {r + 1}: {rows[r].Length} fields, header has {header.Length}.");
            result.Add(parse(new Row(table.File, r + 1, rows[r], index)));
        }

        if (result.Count != table.RowCount)
            throw new ExportFormatException($"{table.File} has {result.Count} rows, manifest.json says {table.RowCount}.");

        return result;
    }

    static void CheckIds(ManifestTable table, List<(long Id, int Line)> ids)
    {
        var dup = ids.GroupBy(x => x.Id).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
            throw new ExportFormatException($"{table.File}: {table.Name} ID {dup.Key} appears on lines {string.Join(", ", dup.Select(d => d.Line))}.");

        long? min = ids.Count == 0 ? null : ids.Min(x => x.Id);
        long? max = ids.Count == 0 ? null : ids.Max(x => x.Id);
        if (min != table.MinId || max != table.MaxId)
            throw new ExportFormatException($"{table.File}: ID range {min}..{max} does not match manifest.json {table.MinId}..{table.MaxId}.");
    }

    static LegacyEmployee ParseEmployee(Row row) => new(
        row.RequiredLong("EmployeeID"),
        row["FirstName"],
        row["LastName"],
        row.Digits("PinCode"),
        row.Has("IsActive") ? row.Flag("IsActive") : null,
        row.Has("IsActive") ? row.FlagRaw("IsActive") : null,
        row.Line);

    static LegacyShift ParseShift(Row row) => new(
        row.RequiredLong("ShiftID"),
        row.OptionalLong("EmployeeID"),
        row.Timestamp("TimeIn"),
        row.Timestamp("TimeOut"),
        new RawShiftTimes(row["TimeIn"], row["TimeIn_OADate"], row["TimeOut"], row["TimeOut_OADate"]),
        row.Line);

    internal static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    static JsonElement Req(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v
            : throw new ExportFormatException($"manifest.json is missing \"{name}\".");

    static string ReqString(JsonElement e, string name) =>
        Req(e, name).GetString() ?? throw new ExportFormatException($"manifest.json \"{name}\" is empty.");

    static string? OptString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long? OptLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    sealed class Row(string file, int line, string?[] fields, Dictionary<string, int> index)
    {
        public int Line => line;

        public bool Has(string column) => index.ContainsKey(column);

        public string? this[string column] => fields[index[column]];

        ExportFormatException Bad(string column, string why) =>
            new($"{file} line {line}, {column}: {why}");

        public long RequiredLong(string column) =>
            OptionalLong(column) ?? throw Bad(column, "value is empty.");

        public long? OptionalLong(string column)
        {
            var s = this[column];
            if (s is null)
                return null;
            return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)
                ? v
                : throw Bad(column, $"'{s}' is not a whole number.");
        }

        /// <summary>An Access Yes/No (<c>true</c>/<c>false</c>) or integer flag, as 1/0. Access stores True as -1.</summary>
        public int? Flag(string column) => FlagRaw(column) switch
        {
            null => null,
            0 => 0,
            _ => 1,
        };

        public long? FlagRaw(string column) => this[column] switch
        {
            null => null,
            "true" or "True" => -1,
            "false" or "False" => 0,
            _ => OptionalLong(column),
        };

        /// <summary>The stored PIN as its decimal digits. Leading zeros were already lost in Access.</summary>
        public string? Digits(string column)
        {
            var s = this[column];
            if (s is null)
                return null;
            if (s.Length == 0 || !s.All(char.IsAsciiDigit))
                throw Bad(column, $"'{s}' is not a non-negative whole number.");
            return s;
        }

        /// <summary>
        /// Reads the ISO text column and cross-checks it against the raw Access
        /// double in the matching <c>_OADate</c> column. Both are written from
        /// the same stored value, so any disagreement means a broken export.
        /// </summary>
        public DateTime? Timestamp(string column)
        {
            var text = this[column];
            var oa = this[column + "_OADate"];
            if (text is null && oa is null)
                return null;
            if (text is null || oa is null)
                throw Bad(column, "text and _OADate columns disagree on whether the value is empty.");

            if (!DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
                throw Bad(column, $"'{text}' is not yyyy-MM-ddTHH:mm:ss.fff.");
            if (!double.TryParse(oa, NumberStyles.Float, CultureInfo.InvariantCulture, out var oaValue))
                throw Bad(column + "_OADate", $"'{oa}' is not a number.");

            DateTime fromOa;
            try
            {
                fromOa = DateTime.FromOADate(oaValue);
            }
            catch (ArgumentException)
            {
                throw Bad(column + "_OADate", $"{oa} is outside the Access date range.");
            }

            if (Math.Abs((fromOa - value).TotalMilliseconds) > 1)
                throw Bad(column, $"text {text} and _OADate {oa} ({fromOa:yyyy-MM-ddTHH:mm:ss.fff}) differ.");

            return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        }
    }
}
