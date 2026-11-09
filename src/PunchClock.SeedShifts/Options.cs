using System.Globalization;
using PunchClock.Data.Sqlite;

namespace PunchClock.SeedShifts;

/// <summary>Command line options; see <see cref="Program"/> for what each one does.</summary>
internal sealed class Options
{
    public string DbPath { get; private set; } = Path.GetFullPath(PunchClockDatabase.ResolvePath());

    /// <summary>First day of the month to fill; null for the month before the current one in site time.</summary>
    public DateTime? Month { get; private set; }

    /// <summary>Distinct, in the order given; empty for every active employee.</summary>
    public List<long> EmployeeIds { get; } = [];

    public string? ActorUsername { get; private set; }

    public Window InWindow { get; private set; } = new(new TimeSpan(5, 0, 0), new TimeSpan(7, 0, 0));

    public Window OutWindow { get; private set; } = new(new TimeSpan(14, 0, 0), new TimeSpan(16, 0, 0));

    public bool Weekends { get; private set; }

    public int? SeedOverride { get; private set; }

    /// <summary>Full path, or null for no CSV.</summary>
    public string? CsvPath { get; private set; }

    public bool Apply { get; private set; }

    public int SeedFor(DateTime monthStart) => SeedOverride ?? monthStart.Year * 100 + monthStart.Month;

    public static Options Parse(params string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            string Value()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{option} needs a value.");
                }

                return args[++i];
            }

            switch (option)
            {
                case "--db":
                    options.DbPath = FullPath(Value(), option);
                    break;
                case "--month":
                    var monthText = Value();
                    options.Month = DateTime.TryParseExact(monthText, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
                        ? month
                        : throw new ArgumentException($"--month must be yyyy-MM, not '{monthText}'.");
                    break;
                case "--employee":
                    var idText = Value();
                    var id = long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : throw new ArgumentException($"--employee must be an employee id, not '{idText}'.");

                    // Seeding an employee twice would plan two overlapping shifts a day (IN, IN, OUT, OUT),
                    // which the reports show as missing punches, not as the complete shifts promised.
                    if (options.EmployeeIds.Contains(id))
                    {
                        throw new ArgumentException($"--employee {id} is given more than once.");
                    }

                    options.EmployeeIds.Add(id);
                    break;
                case "--as":
                    options.ActorUsername = Value();
                    break;
                case "--in":
                    options.InWindow = Window.Parse(Value(), option);
                    break;
                case "--out":
                    options.OutWindow = Window.Parse(Value(), option);
                    break;
                case "--weekends":
                    options.Weekends = true;
                    break;
                case "--seed":
                    var seedText = Value();
                    options.SeedOverride = int.TryParse(seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed)
                        ? seed
                        : throw new ArgumentException($"--seed must be an integer, not '{seedText}'.");
                    break;
                case "--csv":
                    options.CsvPath = FullPath(Value(), option);
                    break;
                case "--apply":
                    options.Apply = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{option}'.");
            }
        }

        if (options.OutWindow.Start <= options.InWindow.End)
        {
            throw new ArgumentException("The --out window must start after the --in window ends, so every shift has a positive length.");
        }

        return options;
    }

    private static string FullPath(string path, string option)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"{option} is not a valid path: '{path}'.", ex);
        }
    }
}

/// <summary>A wall-clock window, inclusive, to the second.</summary>
internal sealed record Window(TimeSpan Start, TimeSpan End)
{
    public static Window Parse(string text, string option)
    {
        var parts = text.Split('-');
        if (parts.Length != 2
            || !TimeSpan.TryParseExact(parts[0], @"hh\:mm", CultureInfo.InvariantCulture, out var start)
            || !TimeSpan.TryParseExact(parts[1], @"hh\:mm", CultureInfo.InvariantCulture, out var end)
            || end < start)
        {
            throw new ArgumentException($"{option} must be HH:mm-HH:mm with the end no earlier than the start, not '{text}'.");
        }

        return new Window(start, end);
    }

    public TimeSpan Pick(Random rng) =>
        Start + TimeSpan.FromSeconds(rng.Next(0, (int)(End - Start).TotalSeconds + 1));
}
