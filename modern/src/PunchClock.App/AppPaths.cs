using System.IO;

namespace PunchClock.App;

internal static class AppPaths
{
    public const string DatabaseOverrideVariable = "PUNCHCLOCK_DB";

    /// <summary>
    /// %ProgramData%\PunchClock\punchclock.db, shared by every Windows account on the
    /// kiosk. Override with the PUNCHCLOCK_DB environment variable.
    /// </summary>
    public static string DatabasePath()
    {
        var overridePath = Environment.GetEnvironmentVariable(DatabaseOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PunchClock",
            "punchclock.db");
    }
}
