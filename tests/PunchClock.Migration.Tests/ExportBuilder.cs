using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PunchClock.Migration.Tests;

/// <summary>
/// Writes an export folder in the legacy exporter's format (CSV + manifest.json
/// with hashes, counts, ID ranges and optional control totals), so tests never
/// need real employee data.
/// </summary>
public sealed class ExportBuilder : IDisposable
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly List<(long Id, string? First, string? Last, string? Pin, string? IsActive)> _employees = [];
    readonly List<(long Id, long? EmployeeId, DateTime? In, DateTime? Out)> _shifts = [];

    public ExportBuilder()
    {
        Folder = Path.Combine(Path.GetTempPath(), "punchclock-migration-tests", Guid.NewGuid().ToString("N"));
    }

    public string Folder { get; }

    public bool WithIsActive { get; set; } = true;

    public bool WithControlTotals { get; set; } = true;

    public string? SiteTimeZone { get; set; } = "America/Toronto";

    public string Tool { get; set; } = "ps-Export-LegacyData/2";

    public List<string> Failures { get; } = [];

    public ExportBuilder Employee(long id, string? first, string? last, string? pin = "1234", string? isActive = "1")
    {
        _employees.Add((id, first, last, pin, isActive));
        return this;
    }

    public ExportBuilder Shift(long id, long? employeeId, string? timeIn, string? timeOut)
    {
        _shifts.Add((id, employeeId, Parse(timeIn), Parse(timeOut)));
        return this;
    }

    /// <summary>Writes the folder and returns the manifest so a test can tamper with it.</summary>
    public JsonObject Build()
    {
        Directory.CreateDirectory(Folder);

        var empColumns = WithIsActive
            ? new[] { "EmployeeID", "FirstName", "LastName", "PinCode", "IsActive" }
            : ["EmployeeID", "FirstName", "LastName", "PinCode"];
        var empCsv = Csv(empColumns, _employees.Select(e =>
        {
            var row = new List<string> { e.Id.ToString(Inv), Field(e.First), Field(e.Last), Field(e.Pin) };
            if (WithIsActive)
                row.Add(Field(e.IsActive));
            return row;
        }));

        string[] shiftColumns = ["ShiftID", "EmployeeID", "TimeIn", "TimeIn_OADate", "TimeOut", "TimeOut_OADate"];
        var shiftCsv = Csv(shiftColumns, _shifts.Select(s => new List<string>
        {
            s.Id.ToString(Inv), s.EmployeeId?.ToString(Inv) ?? "", Iso(s.In), Oa(s.In), Iso(s.Out), Oa(s.Out),
        }));

        File.WriteAllText(Path.Combine(Folder, "Employee.csv"), empCsv, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(Folder, "Shift.csv"), shiftCsv, new UTF8Encoding(false));

        var employeeTotals = new JsonObject
        {
            ["EmployeeID"] = Totals(_employees.Count, _employees.Sum(e => e.Id)),
            ["FirstName"] = Totals(_employees.Count(e => e.First is not null)),
            ["LastName"] = Totals(_employees.Count(e => e.Last is not null)),
            ["PinCode"] = Totals(_employees.Count(e => e.Pin is not null), _employees.Where(e => e.Pin is not null).Sum(e => long.Parse(e.Pin!, Inv))),
        };
        if (WithIsActive)
            employeeTotals["IsActive"] = Totals(_employees.Count(e => e.IsActive is not null), _employees.Where(e => e.IsActive is not null).Sum(e => long.Parse(e.IsActive!, Inv)));

        var ins = _shifts.Where(s => s.In is not null).Select(s => s.In!.Value).ToList();
        var outs = _shifts.Where(s => s.Out is not null).Select(s => s.Out!.Value).ToList();
        var shiftTotals = new JsonObject
        {
            ["ShiftID"] = Totals(_shifts.Count, _shifts.Sum(s => s.Id)),
            ["EmployeeID"] = Totals(_shifts.Count(s => s.EmployeeId is not null), _shifts.Sum(s => s.EmployeeId ?? 0)),
            ["TimeIn"] = DateTotals(ins),
            ["TimeOut"] = DateTotals(outs),
        };

        var manifest = new JsonObject
        {
            ["tool"] = Tool,
            ["exported_at_utc"] = "2026-10-02T19:00:00.000Z",
            ["source"] = new JsonObject
            {
                ["path"] = @"C:\PunchClock\PunchClock.accdb",
                ["size_bytes"] = 1466368,
                ["sha256"] = new string('a', 64),
                ["snapshot"] = "source/PunchClock.accdb",
            },
            ["tables"] = new JsonArray(
                Table("Employee", "EmployeeID", empColumns, empCsv, _employees.Select(e => e.Id).ToList(), WithControlTotals ? employeeTotals : null),
                Table("Shift", "ShiftID", shiftColumns, shiftCsv, _shifts.Select(s => s.Id).ToList(), WithControlTotals ? shiftTotals : null)),
            ["warnings"] = new JsonArray(),
            ["failures"] = new JsonArray(Failures.Select(f => (JsonNode)f).ToArray()),
        };
        if (SiteTimeZone is not null)
            manifest["site_time_zone"] = new JsonObject { ["id"] = SiteTimeZone, ["source"] = "export machine" };

        WriteManifest(manifest);
        return manifest;
    }

    public void WriteManifest(JsonObject manifest) =>
        File.WriteAllText(Path.Combine(Folder, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    public void Dispose()
    {
        if (Directory.Exists(Folder))
            Directory.Delete(Folder, recursive: true);
    }

    static JsonObject Table(string name, string key, string[] columns, string csv, List<long> ids, JsonObject? totals)
    {
        var t = new JsonObject
        {
            ["name"] = name,
            ["file"] = $"{name}.csv",
            ["row_count"] = ids.Count,
            ["primary_key"] = key,
            ["min_id"] = ids.Count == 0 ? null : ids.Min(),
            ["max_id"] = ids.Count == 0 ? null : ids.Max(),
            ["columns"] = new JsonArray(columns.Select(c => (JsonNode)c).ToArray()),
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(csv))),
        };
        if (totals is not null)
            t["control_totals"] = totals;
        return t;
    }

    static JsonObject Totals(long nonNull, long? sum = null)
    {
        var o = new JsonObject { ["non_null"] = nonNull };
        if (sum is not null)
            o["sum"] = nonNull == 0 ? null : sum.Value.ToString(Inv);
        return o;
    }

    static JsonObject DateTotals(List<DateTime> values) => new()
    {
        ["non_null"] = values.Count,
        ["min"] = values.Count == 0 ? null : Iso(values.Min()),
        ["max"] = values.Count == 0 ? null : Iso(values.Max()),
    };

    static string Csv(string[] header, IEnumerable<List<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', header)).Append("\r\n");
        foreach (var r in rows)
            sb.Append(string.Join(',', r)).Append("\r\n");
        return sb.ToString();
    }

    static string Field(string? s) =>
        s is null ? "" : s.Length == 0 || s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? '"' + s.Replace("\"", "\"\"") + '"' : s;

    static DateTime? Parse(string? s) =>
        s is null ? null : DateTime.ParseExact(s, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff"], Inv, DateTimeStyles.None);

    static string Iso(DateTime? d) => d?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", Inv) ?? "";

    static string Oa(DateTime? d) => d?.ToOADate().ToString("R", Inv) ?? "";
}
