namespace PunchClock.SeedShifts;

/// <summary>Keeps <c>--csv</c> from overwriting the database it describes, or any other database.</summary>
internal static class CsvTarget
{
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Throws when <paramref name="csvPath"/> is the database, one of its SQLite side files, a
    /// folder, or an existing SQLite database under any name (another spelling, a link, a copy).
    /// </summary>
    public static void EnsureNotDatabase(string csvPath, string dbPath)
    {
        var csv = Canonical(csvPath);
        var db = Canonical(dbPath);
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            if (string.Equals(csv, db + suffix, PathComparison))
            {
                throw new SeedException($"--csv {csvPath} is the database{(suffix.Length == 0 ? "" : $"'s {suffix[1..]} file")}; choose another file.", exitCode: 2);
            }
        }

        if (Directory.Exists(csv))
        {
            throw new SeedException($"--csv {csvPath} is a folder; give a file name.", exitCode: 2);
        }

        if (IsSqliteDatabase(csv))
        {
            throw new SeedException($"--csv {csvPath} is an existing SQLite database; refusing to overwrite it.", exitCode: 2);
        }
    }

    private static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            return new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        }
        catch (IOException)
        {
            // A broken or looping link: compare the path as given; the header check still applies.
            return full;
        }
    }

    private static bool IsSqliteDatabase(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[SqliteHeader.Length];
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
            && header.AsSpan().SequenceEqual(SqliteHeader);
    }
}
