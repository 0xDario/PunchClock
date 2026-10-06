using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;
using PunchClock.Core.Persistence;

namespace PunchClock.Data.Sqlite;

/// <param name="AuditHeadSeq">The last audit row in the backup; the live log's BACKUP event names it.</param>
public sealed record BackupResult(string Path, long Bytes, string Sha256, long AuditHeadSeq, string AuditHeadHash);

/// <summary>Admin tasks on the database file itself: verified backups and the review-only verifier warnings.</summary>
public sealed class DatabaseMaintenance(SqliteDatabase database, IPunchClockStore store)
{
    /// <summary>
    /// Writes a consistent copy of the live database to <paramref name="path"/> (SQLite
    /// <c>VACUUM INTO</c>: one read snapshot, safe while the kiosk is punching), then opens the
    /// copy read-only and checks its schema fingerprint and every verifier view before keeping
    /// it. Last, a <c>BACKUP</c> audit event in the live log records the file, its SHA-256 and
    /// the audit head it contains, so a restored copy can be matched to the log. Admins only:
    /// the copy holds every PIN and password hash.
    /// </summary>
    /// <exception cref="IOException"><paramref name="path"/> already exists; SQLite will not overwrite it.</exception>
    /// <exception cref="AuditIntegrityException">The copy does not verify; it is deleted.</exception>
    public async Task<BackupResult> BackupAsync(AppUser by, string path, CancellationToken ct = default)
    {
        if (by is not { Role: UserRole.Admin, IsActive: true })
        {
            throw new UnauthorizedAccessException("Only admins can back up the database.");
        }

        path = System.IO.Path.GetFullPath(path);
        if (string.Equals(path, database.Path, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Choose a different file from the live database.");
        }

        if (File.Exists(path))
        {
            throw new IOException($"{path} already exists. Choose a new file name.");
        }

        await using (var connection = await database.OpenAsync(ct: ct))
        {
            await using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM INTO $path;";
            vacuum.Parameters.AddWithValue("$path", path);
            await vacuum.ExecuteNonQueryAsync(ct);
        }

        BackupResult result;
        try
        {
            result = await VerifyCopyAsync(path, ct);
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            throw;
        }

        await using var uow = await store.BeginAsync(ct);
        uow.ActAs(AuditActor.ForUser(by.Id));
        await uow.RecordEventAsync(AuditEvent.Backup, JsonSerializer.Serialize(new
        {
            file = result.Path,
            bytes = result.Bytes,
            sha256 = result.Sha256,
            audit_head_seq = result.AuditHeadSeq,
            audit_head_hash = result.AuditHeadHash,
        }), ct);
        await uow.CommitAsync(ct);
        return result;
    }

    /// <returns>
    /// Rows from <see cref="AuditVerifier.WarningViews"/>: a system clock that ran backwards, or a
    /// punch offset that no longer matches today's time zone rules. Not proof of tampering, so
    /// the app keeps running; an admin should review each one.
    /// </returns>
    public Task<IReadOnlyList<string>> FindWarningsAsync(CancellationToken ct = default) =>
        AuditVerifier.FindWarningsAsync(database, ct);

    private static async Task<BackupResult> VerifyCopyAsync(string path, CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        long seq;
        string hash;
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(ct);
            await SqliteDatabase.ConfigureAsync(connection, new AuditContext(AuditContext.DefaultClient, AuditActor.System, "backup check"), ct);
            await SchemaMigrator.EnsureSchemaIntactAsync(connection, null, ct);
            var problems = await AuditVerifier.FindProblemsAsync(connection, ct);
            if (problems.Count > 0)
            {
                throw new AuditIntegrityException(problems);
            }

            await using var head = connection.CreateCommand();
            head.CommandText = "SELECT seq, row_hash FROM audit_log ORDER BY seq DESC LIMIT 1;";
            await using var reader = await head.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            seq = reader.GetInt64(0);
            hash = reader.GetString(1);
        }

        await using var file = File.OpenRead(path);
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        return new BackupResult(path, file.Length, sha256, seq, hash);
    }
}
