using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using PunchClock.Core.Security;
using PunchClock.Data.Sqlite;
using PunchClock.Migration.Analysis;
using PunchClock.Migration.Database;
using PunchClock.Migration.Export;
using PunchClock.Migration.Reporting;

namespace PunchClock.Import;

/// <summary>
/// One-time cutover from the legacy Access app. Double-clicked (no arguments)
/// it walks through the steps; with arguments it runs unattended:
/// <code>
/// PunchClock.Import check  &lt;export folder or zip&gt; [--time-zone ID] [--report DIR]
/// PunchClock.Import import &lt;export folder or zip&gt; [--time-zone ID] [--report DIR] [--db PATH] --yes
/// </code>
/// Exit codes: 0 ok, 1 unexpected error, 2 export rejected, 3 import refused, 4 stopped by the user,
/// 5 imported but a report file could not be written.
/// </summary>
internal static class Program
{
    static readonly string Version =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    static async Task<int> Main(string[] args)
    {
        var interactive = args.Length == 0;
        try
        {
            var options = interactive ? Ask() : Options.Parse(args);
            if (options is null)
                return 4;
            return await RunAsync(options, interactive);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Options.Usage);
            return 1;
        }
        catch (ExportFormatException ex)
        {
            return Fail(2, "The export folder cannot be imported", ex.Message, interactive);
        }
        catch (ImportRefusedException ex)
        {
            return Fail(3, "Import refused, nothing was written", ex.Message, interactive);
        }
        catch (Exception ex)
        {
            return Fail(1, "Import failed, nothing was written", ex.ToString(), interactive);
        }
    }

    static async Task<int> RunAsync(Options o, bool interactive)
    {
        Console.WriteLine($"PunchClock legacy import {Version}");
        Console.WriteLine();

        using var source = ExportSource.Open(o.ExportPath);
        var export = LegacyExport.Load(source.Folder);

        var (zone, zoneSource) = ResolveZone(o.TimeZone, export.Manifest.SiteTimeZoneId);
        if (interactive)
            (zone, zoneSource) = ConfirmZone(zone, zoneSource);

        var plan = ImportAnalyzer.Analyze(export, zone);
        PrintSummary(plan, zone, zoneSource);

        var exportName = Path.GetFileName(Path.TrimEndingDirectorySeparator(o.ExportPath));
        var reportBase = o.ReportDir ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(o.ExportPath))!, Path.GetFileNameWithoutExtension(exportName));

        if (!o.Import)
        {
            var dir = reportBase + "-check";
            ImportReport.Write(dir, plan, zoneSource, outcome: null);
            Console.WriteLine($"Check report: {Path.Combine(dir, ImportReport.ReportFile)}");
            if (interactive)
                OpenInNotepad(Path.Combine(dir, ImportReport.ReportFile));
            if (!interactive || !Confirm($"Type IMPORT to write this into {o.DatabasePath ?? PunchClockDatabase.ResolvePath()}, or press Enter to stop: ", "IMPORT"))
                return interactive ? Done(4, "Stopped. Nothing was written.") : 0;
        }
        else if (!o.Yes)
        {
            throw new UsageException("import writes to the database; add --yes to confirm.");
        }

        Console.WriteLine();
        Console.WriteLine("Importing...");
        var database = await PunchClockDatabase.OpenAndMigrateAsync(o.DatabasePath);
        var importer = new LegacyImporter(new Pbkdf2PinHasher(), $"PunchClock.Import {Version}");
        var outcome = await importer.ImportAsync(plan, database);

        // Committed. From here on nothing may claim the import failed: a second
        // import is refused, so the operator must not fall back to the old app.
        Console.WriteLine();
        Console.WriteLine($"Imported {plan.Employees.Count} employees and {outcome.PunchesWritten} punches into {outcome.DatabasePath}");
        Console.WriteLine($"Audit log head: seq {outcome.ChainSeq}, {outcome.ChainHash}");
        if (outcome.TemporaryPins.Count > 0)
        {
            Console.WriteLine("Temporary PINs for employees who had none in the old app (they must choose a new one before their first punch is recorded):");
            foreach (var (id, pin) in outcome.TemporaryPins)
                Console.WriteLine($"  Employee {id}: {pin}");
        }

        // Keep the evidence next to the database, where backups of it will pick it up.
        // That copy never lists the temporary PINs; the one next to the export does.
        var stamp = outcome.ImportedAtUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var kept = Path.Combine(Path.GetDirectoryName(database.Path)!, "imports", $"import-{stamp}");
        var report = reportBase + "-import";
        var keptOk = TryWrite("copy kept with the database", kept, () =>
        {
            ImportReport.Write(kept, plan, zoneSource, outcome);
            File.Copy(Path.Combine(source.Folder, "manifest.json"), Path.Combine(kept, "manifest.json"));
        });
        var reportOk = TryWrite("report", report, () => ImportReport.Write(report, plan, zoneSource, outcome, withTemporaryPins: true));

        if (!keptOk || !reportOk)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("The import itself is complete and saved; only the files above are missing.");
            Console.Error.WriteLine("Do not go back to the old app. Write down the audit log head and any temporary PINs shown above,");
            Console.Error.WriteLine("then rerun `PunchClock.Import check` on the export to recreate the hours report.");
            return interactive ? Done(5, "") : 5;
        }

        Console.WriteLine($"Report: {Path.Combine(report, ImportReport.ReportFile)}");
        Console.WriteLine($"Copy kept with the database: {kept}");
        if (interactive)
            OpenInNotepad(Path.Combine(report, ImportReport.ReportFile));
        return interactive ? Done(0, "Done. Print the report and compare its hours with the old app's report.") : 0;
    }

    static bool TryWrite(string what, string directory, Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not write the {what} to {directory}: {ex.Message}");
            return false;
        }
    }

    static (TimeZoneInfo Zone, string Source) ResolveZone(string? requested, string? fromManifest)
    {
        if (requested is not null)
            return (FindZone(requested), "--time-zone");
        if (fromManifest is not null)
            return (FindZone(fromManifest), "recorded by the exporter on the old PC");
        return (TimeZoneInfo.Local, "this PC's time zone; the export did not record one");
    }

    /// <summary>The schema checks offsets against Windows zone IDs, so an IANA ID is stored as its Windows equivalent.</summary>
    static TimeZoneInfo FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows) ? windows : id);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new UsageException($"Unknown time zone '{id}'. Use a Windows ID such as \"Eastern Standard Time\" (run `tzutil /l` to list them).");
        }
    }

    static (TimeZoneInfo, string) ConfirmZone(TimeZoneInfo zone, string source)
    {
        Console.WriteLine($"The old app saved local times without a time zone. They will be read as:");
        Console.WriteLine($"  {zone.DisplayName}  ({source})");
        while (true)
        {
            Console.Write("Press Enter if that is where the punches were recorded, or type a Windows time zone ID: ");
            var answer = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(answer))
                return (zone, source);
            try
            {
                return (FindZone(answer), "entered at import");
            }
            catch (UsageException)
            {
                Console.WriteLine($"  '{answer}' is not a time zone on this PC.");
            }
        }
    }

    static void PrintSummary(ImportPlan plan, TimeZoneInfo zone, string zoneSource)
    {
        var t = plan.Totals;
        var review = plan.Findings.Count(f => f.Level == FindingLevel.Review);
        Console.WriteLine();
        Console.WriteLine($"Export verified: hashes, row counts and ID ranges match {Path.Combine(plan.Export.Folder, "manifest.json")}");
        Console.WriteLine($"  Source database SHA-256  {plan.Export.Manifest.SourceSha256}");
        Console.WriteLine($"  Employees                {t.EmployeeRows}");
        Console.WriteLine($"  Shifts                   {t.ShiftRows} ({t.SkippedShiftRows} skipped, kept as evidence)");
        Console.WriteLine($"  Hours (old report rules) {ImportReport.Hours(t.TotalWallClock)}");
        Console.WriteLine($"  Time zone                {zone.Id} ({zoneSource})");
        Console.WriteLine($"  Needs review afterwards  {review} item(s), listed in the report");
        var future = plan.Findings.Count(f => f.Code == FindingCode.FutureTime);
        if (future > 0)
            Console.WriteLine($"  CANNOT IMPORT            {future} shift(s) dated after today; see the report");
        Console.WriteLine();
    }

    static bool Confirm(string prompt, string word)
    {
        Console.WriteLine();
        Console.Write(prompt);
        return string.Equals(Console.ReadLine()?.Trim(), word, StringComparison.OrdinalIgnoreCase);
    }

    static Options? Ask()
    {
        Console.WriteLine("PunchClock: move data from the old app into the new one.");
        Console.WriteLine();
        Console.WriteLine("You need the export folder or .zip made by run-export.cmd (on the Desktop by default).");
        Console.Write("Drag it onto this window, then press Enter: ");
        var path = Console.ReadLine()?.Trim().Trim('"');
        if (string.IsNullOrEmpty(path))
            return null;
        return new Options(Import: false, path, null, null, null, Yes: false);
    }

    static void OpenInNotepad(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch
        {
            // Opening the report is a convenience; its path is already on screen.
        }
    }

    static int Fail(int code, string title, string detail, bool interactive)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"{title}:");
        Console.Error.WriteLine($"  {detail}");
        return interactive ? Done(code, "") : code;
    }

    static int Done(int code, string message)
    {
        if (message.Length > 0)
            Console.WriteLine(message);
        Console.WriteLine();
        Console.Write("Press Enter to close.");
        Console.ReadLine();
        return code;
    }

    sealed class UsageException(string message) : Exception(message);

    sealed record Options(bool Import, string ExportPath, string? TimeZone, string? ReportDir, string? DatabasePath, bool Yes)
    {
        public const string Usage = """
            Usage:
              PunchClock.Import                       (step by step)
              PunchClock.Import check  <export folder or .zip> [--time-zone ID] [--report DIR]
              PunchClock.Import import <export folder or .zip> [--time-zone ID] [--report DIR] [--db PATH] --yes
            """;

        public static Options Parse(string[] args)
        {
            if (args[0] is not ("check" or "import"))
                throw new UsageException($"Unknown command '{args[0]}'.");
            string? path = null, zone = null, report = null, db = null;
            var yes = false;
            for (var i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException($"{args[i]} needs a value.");
                switch (args[i])
                {
                    case "--time-zone": zone = Next(); break;
                    case "--report": report = Next(); break;
                    case "--db": db = Next(); break;
                    case "--yes": yes = true; break;
                    case var a when a.StartsWith("--", StringComparison.Ordinal): throw new UsageException($"Unknown option {a}.");
                    case var a when path is null: path = a; break;
                    default: throw new UsageException($"Unexpected argument {args[i]}.");
                }
            }

            return new Options(args[0] == "import", path ?? throw new UsageException("Give the export folder or .zip."), zone, report, db, yes);
        }
    }

    /// <summary>The export as a folder; a .zip from the exporter is unpacked to a temp folder first.</summary>
    sealed class ExportSource : IDisposable
    {
        string? _temp;

        public required string Folder { get; init; }

        public static ExportSource Open(string path)
        {
            if (Directory.Exists(path))
                return new ExportSource { Folder = path };
            if (!File.Exists(path) || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new ExportFormatException($"{path} is not an export folder or .zip.");

            var temp = Path.Combine(Path.GetTempPath(), "punchclock-import-" + Guid.NewGuid().ToString("N"));
            var source = new ExportSource { Folder = temp, _temp = temp };
            try
            {
                ZipFile.ExtractToDirectory(path, temp);
                var folder = File.Exists(Path.Combine(temp, "manifest.json"))
                    ? temp
                    : Directory.GetDirectories(temp).SingleOrDefault(d => File.Exists(Path.Combine(d, "manifest.json")))
                      ?? throw new ExportFormatException($"{Path.GetFileName(path)} has no manifest.json.");
                return new ExportSource { Folder = folder, _temp = temp };
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Never throws: this also runs after an import has committed, where an
        /// exception would reach Main and wrongly report that nothing was written.
        /// </summary>
        public void Dispose()
        {
            // The unpacked copy holds plaintext PINs; do not leave it in %TEMP%.
            if (_temp is null || !Directory.Exists(_temp))
                return;
            try
            {
                foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_temp, recursive: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not delete the unpacked export at {_temp} ({ex.Message}).");
                Console.Error.WriteLine("It holds the old PINs in plain text: delete that folder by hand.");
            }
        }
    }
}
