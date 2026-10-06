using System.Text.Json.Nodes;
using PunchClock.Migration.Export;

namespace PunchClock.Migration.Tests;

public sealed class LegacyExportTests : IDisposable
{
    readonly ExportBuilder _export = new();

    public void Dispose() => _export.Dispose();

    ExportBuilder Sample() => _export
        .Employee(14, "Ann", "Lee", "123123")
        .Employee(15, "", null, "123", "0")
        .Shift(30, 14, "2025-01-06 08:00:00", "2025-01-06 08:00:00")
        .Shift(31, 14, "2025-01-07 08:00:00", "2025-01-07 16:30:00")
        .Shift(32, 15, "2025-01-07 09:00:00", null);

    [Fact]
    public void Loads_every_row_with_legacy_ids_and_raw_values()
    {
        Sample().Build();

        var export = LegacyExport.Load(_export.Folder);

        Assert.Equal([14L, 15L], export.Employees.Select(e => e.EmployeeId));
        Assert.Equal([30L, 31L, 32L], export.Shifts.Select(s => s.ShiftId));
        Assert.Equal("123123", export.Employees[0].PinCode);
        Assert.Equal(0, export.Employees[1].IsActive);
        Assert.Null(export.Shifts[2].TimeOut);
        Assert.Equal(new DateTime(2025, 1, 7, 16, 30, 0), export.Shifts[1].TimeOut);
        Assert.Equal("America/Toronto", export.Manifest.SiteTimeZoneId);
        Assert.True(export.SnapshotVerified);
    }

    [Fact]
    public void Keeps_empty_string_distinct_from_null()
    {
        Sample().Build();

        var e = LegacyExport.Load(_export.Folder).Employees[1];

        Assert.Equal("", e.FirstName);
        Assert.Null(e.LastName);
    }

    [Fact]
    public void Rejects_a_csv_edited_after_export()
    {
        Sample().Build();
        File.AppendAllText(Path.Combine(_export.Folder, "Shift.csv"), "33,14,,,,\r\n");

        var ex = Assert.Throws<ExportFormatException>(() => LegacyExport.Load(_export.Folder));
        Assert.Contains("SHA-256", ex.Message);
    }

    [Fact]
    public void Rejects_a_row_count_that_disagrees_with_the_manifest()
    {
        var manifest = Sample().Build();
        manifest["tables"]![1]!["row_count"] = 4;
        _export.WriteManifest(manifest);

        var ex = Assert.Throws<ExportFormatException>(() => LegacyExport.Load(_export.Folder));
        Assert.Contains("3 rows", ex.Message);
    }

    [Fact]
    public void Rejects_an_export_that_failed_its_own_checks()
    {
        _export.Failures.Add("The source database changed during the export.");
        Sample().Build();

        var ex = Assert.Throws<ExportFormatException>(() => LegacyExport.Load(_export.Folder));
        Assert.Contains("changed during the export", ex.Message);
    }

    [Fact]
    public void Rejects_control_totals_that_disagree_with_the_rows()
    {
        var manifest = Sample().Build();
        manifest["tables"]![1]!["control_totals"]!["TimeOut"]!["non_null"] = 3;
        _export.WriteManifest(manifest);

        var ex = Assert.Throws<ExportFormatException>(() => LegacyExport.Load(_export.Folder));
        Assert.Contains("control total for TimeOut", ex.Message);
    }

    [Fact]
    public void Rejects_an_active_flag_that_disagrees_with_the_access_sum()
    {
        // Same nullity, different value: only the sum can tell.
        var manifest = Sample().Build();
        manifest["tables"]![0]!["control_totals"]!["IsActive"]!["sum"] = "2";
        _export.WriteManifest(manifest);

        var ex = Assert.Throws<ExportFormatException>(() => LegacyExport.Load(_export.Folder));
        Assert.Contains("control total for IsActive", ex.Message);
    }

    [Fact]
    public void Rejects_an_id_range_that_disagrees_with_the_manifest()
    {
        var manifest = Sample().Build();
        manifest["tables"]![0]!["max_id"] = 16;
        _export.WriteManifest(manifest);

        Assert.Throws<ExportFormatException>(() => LegacyExport.Load(_export.Folder));
    }

    [Fact]
    public void Rejects_iso_text_that_disagrees_with_the_access_double()
    {
        Sample().Build();
        var path = Path.Combine(_export.Folder, "Shift.csv");
        var text = File.ReadAllText(path).Replace("2025-01-07T16:30:00.000", "2025-01-07T17:30:00.000");
        File.WriteAllText(path, text);
        RehashShiftCsv(text);

        var ex = Assert.Throws<ExportFormatException>(() => LegacyExport.Load(_export.Folder));
        Assert.Contains("differ", ex.Message);
    }

    [Fact]
    public void Accepts_the_older_exporter_without_control_totals_or_time_zone()
    {
        _export.WithControlTotals = false;
        _export.SiteTimeZone = null;
        _export.WithIsActive = false;
        _export.Tool = "jackcess-LegacyExport/1";
        Sample().Build();

        var export = LegacyExport.Load(_export.Folder);

        Assert.Null(export.Manifest.SiteTimeZoneId);
        Assert.All(export.Employees, e => Assert.Null(e.IsActive));
    }

    [Fact]
    public void Parses_quoted_fields_with_commas_quotes_and_newlines()
    {
        var rows = Csv.Parse("a,b,c\r\n\"x,1\",\"say \"\"hi\"\"\",\"two\r\nlines\"\r\n,\"\",z", "t.csv");

        Assert.Equal(3, rows.Count);
        Assert.Equal(["x,1", "say \"hi\"", "two\r\nlines"], rows[1]);
        Assert.Equal([null, "", "z"], rows[2]);
    }

    void RehashShiftCsv(string text)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(_export.Folder, "manifest.json")))!.AsObject();
        manifest["tables"]![1]!["sha256"] = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        _export.WriteManifest(manifest);
    }
}
