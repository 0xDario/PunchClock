namespace PunchClock.Data.Sqlite;

/// <summary>
/// Shared startup for every process that touches the database (the kiosk, the Access importer):
/// one rule for where the file lives, and the schema migrated before anything reads it.
/// </summary>
public static class PunchClockDatabase
{
    public const string PathVariable = "PUNCHCLOCK_DB";

    /// <summary>
    /// <c>%PUNCHCLOCK_DB%</c> when set, else <c>%ProgramData%\PunchClock\punchclock.db</c>:
    /// machine-wide, so every Windows account on the kiosk sees the same punches.
    /// </summary>
    public static string ResolvePath()
    {
        var path = Environment.GetEnvironmentVariable(PathVariable);
        return string.IsNullOrWhiteSpace(path)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PunchClock",
                "punchclock.db")
            : System.IO.Path.GetFullPath(path);
    }

    /// <summary>
    /// Creates the folder and file if needed, applies pending migrations, and runs the audit
    /// verifier. Throws <see cref="SchemaMismatchException"/> or <see cref="AuditIntegrityException"/>
    /// rather than open a database it cannot vouch for.
    /// </summary>
    /// <param name="path">Database file; <see cref="ResolvePath"/> when null.</param>
    public static async Task<SqliteDatabase> OpenAndMigrateAsync(string? path = null, CancellationToken ct = default)
    {
        var database = new SqliteDatabase(path ?? ResolvePath());
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(database.Path)!);
        await new SchemaMigrator(database).MigrateAsync(ct);

        var problems = await AuditVerifier.FindProblemsAsync(database, ct);
        if (problems.Count > 0)
        {
            throw new AuditIntegrityException(problems);
        }

        return database;
    }
}
