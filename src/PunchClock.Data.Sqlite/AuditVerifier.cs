using System.Globalization;

namespace PunchClock.Data.Sqlite;

public sealed class AuditIntegrityException(IReadOnlyList<string> problems) : Exception(
    $"The audit log does not verify ({problems.Count} problem{(problems.Count == 1 ? "" : "s")}): "
    + string.Join("; ", problems.Take(5))
    + (problems.Count > 5 ? "; ..." : "")
    + ". Nothing was written. Restore the database from a backup or have it examined.")
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// Runs the schema's verifier views, each of which returns no rows on an intact database:
/// the hash chain, rows against their audited images, before/after continuity, events the
/// trigger path cannot produce, and the payroll admission rules re-derived from the log
/// (which catches data let in while a trigger was dropped and then recreated identically,
/// something the schema fingerprint cannot see). <c>verify_clock_v</c> is a warning, not proof
/// of tampering, so it is not run here.
/// </summary>
public static class AuditVerifier
{
    public static readonly IReadOnlyList<string> Views =
        ["verify_chain_v", "verify_drift_v", "verify_continuity_v", "verify_history_v", "verify_rules_v"];

    /// <returns>One line per problem, prefixed with the view that found it; empty when intact.</returns>
    public static async Task<IReadOnlyList<string>> FindProblemsAsync(SqliteDatabase database, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct: ct);

        // One read snapshot for all views; under WAL it does not block kiosk writes.
        await using var transaction = connection.BeginTransaction(deferred: true);
        var problems = new List<string>();
        foreach (var view in Views)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT * FROM {view};";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var fields = Enumerable.Range(0, reader.FieldCount)
                    .Where(i => !reader.IsDBNull(i))
                    .Select(i => $"{reader.GetName(i)}={Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)}");
                problems.Add($"{view}: {string.Join(", ", fields)}");
            }
        }

        return problems;
    }
}
