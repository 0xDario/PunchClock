using PunchClock.Core.Accounts;
using PunchClock.Core.Audit;
using PunchClock.Core.Persistence;
using PunchClock.Core.Site;

namespace PunchClock.Core.Punches;

public enum CorrectionResult
{
    Corrected,

    /// <summary>Only active managers and admins correct punches.</summary>
    NotAllowed,

    /// <summary>The account is linked to this employee; someone else must correct their punches.</summary>
    OwnPunches,

    /// <summary>Shorter than <see cref="PunchCorrectionService.MinReasonLength"/> characters.</summary>
    ReasonTooShort,

    SiteTimeZoneNotSet,

    /// <summary>The local time does not exist in the site zone (skipped by a daylight-saving change).</summary>
    InvalidLocalTime,

    InFuture,

    /// <summary>The punch does not exist or a correction has already superseded it.</summary>
    PunchNotFound,

    EmployeeNotFound,
}

/// <summary>An effective punch and, when it has one, its <c>punch_exception_v</c> kind (missing_out, long_shift...).</summary>
public sealed record PunchReviewRow(Punch Punch, string? Issue);

/// <summary>
/// Manager punch edits. Nothing is ever changed in place: each edit is a <c>punch_correction</c>
/// row that adds, supersedes or voids a punch, carries a reason, and is attributed to the
/// signed-in manager by the audit triggers, which also enforce who may correct whom.
/// Times are entered as site-local wall time and stored as UTC plus the site offset.
/// </summary>
public sealed class PunchCorrectionService(IPunchClockStore store, TimeProvider clock)
{
    /// <summary>The schema's minimum, after trimming.</summary>
    public const int MinReasonLength = 10;

    /// <summary>Effective punches in a UTC range, each with its review issue if any.</summary>
    public async Task<IReadOnlyList<PunchReviewRow>> ListAsync(
        long employeeId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        await using var uow = await store.BeginAsync(ct);
        var issues = (await uow.ListPunchExceptionsAsync(employeeId, ct))
            .GroupBy(e => e.PunchId)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(e => e.Kind)));
        return (await uow.ListEffectivePunchesAsync(employeeId, fromUtc, toUtc, ct))
            .Select(p => new PunchReviewRow(p, issues.GetValueOrDefault(p.Id)))
            .ToList();
    }

    /// <summary>Records a punch the employee missed, such as a forgotten punch-out.</summary>
    public Task<CorrectionResult> AddAsync(
        AppUser by, long employeeId, PunchDirection direction, DateTime siteLocalTime, string reason, CancellationToken ct = default) =>
        ApplyAsync(by, employeeId, targetPunchId: null, CorrectionAction.Add, direction, siteLocalTime, reason, ct);

    /// <summary>Supersedes a punch with one at a different time or direction.</summary>
    public Task<CorrectionResult> AdjustAsync(
        AppUser by, long punchId, PunchDirection direction, DateTime siteLocalTime, string reason, CancellationToken ct = default) =>
        ApplyAsync(by, employeeId: null, punchId, CorrectionAction.Adjust, direction, siteLocalTime, reason, ct);

    /// <summary>Supersedes a punch with nothing, for a punch that should not have happened.</summary>
    public Task<CorrectionResult> VoidAsync(AppUser by, long punchId, string reason, CancellationToken ct = default) =>
        ApplyAsync(by, employeeId: null, punchId, CorrectionAction.Void, null, null, reason, ct);

    private async Task<CorrectionResult> ApplyAsync(
        AppUser by, long? employeeId, long? targetPunchId, CorrectionAction action,
        PunchDirection? direction, DateTime? siteLocalTime, string reason, CancellationToken ct)
    {
        if (by.Role is not (UserRole.Admin or UserRole.Manager) || !by.IsActive)
        {
            return CorrectionResult.NotAllowed;
        }

        if ((reason?.Trim().Length ?? 0) < MinReasonLength)
        {
            return CorrectionResult.ReasonTooShort;
        }

        await using var uow = await store.BeginAsync(ct);

        if (targetPunchId is { } punchId)
        {
            var target = await uow.FindEffectivePunchAsync(punchId, ct);
            if (target is null)
            {
                return CorrectionResult.PunchNotFound;
            }

            employeeId = target.EmployeeId;
        }
        else if (await uow.FindEmployeeAsync(employeeId!.Value, ct) is null)
        {
            return CorrectionResult.EmployeeNotFound;
        }

        if (by.EmployeeId == employeeId)
        {
            return CorrectionResult.OwnPunches;
        }

        DateTimeOffset? occurredUtc = null;
        int? offset = null;
        if (siteLocalTime is { } local)
        {
            var zoneId = await uow.GetSettingAsync(SiteSettingKeys.TimeZoneId, ct);
            if (zoneId is null or SiteSettingKeys.Unset || !TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone))
            {
                return CorrectionResult.SiteTimeZoneNotSet;
            }

            if (ToUtc(local, zone) is not { } utc)
            {
                return CorrectionResult.InvalidLocalTime;
            }

            if (utc > clock.GetUtcNow())
            {
                return CorrectionResult.InFuture;
            }

            occurredUtc = utc;
            offset = (int)zone.GetUtcOffset(utc).TotalMinutes;
        }

        uow.ActAs(AuditActor.ForUser(by.Id), reason!.Trim());
        await uow.AddCorrectionAsync(new NewCorrection(
            action, employeeId!.Value, targetPunchId, direction, occurredUtc, offset, reason.Trim(), by.Id), ct);
        await uow.CommitAsync(ct);
        return CorrectionResult.Corrected;
    }

    /// <summary>
    /// Site wall time to UTC, to the millisecond. Null for a time skipped by a daylight-saving
    /// change. A repeated time (the hour when clocks go back) takes its first occurrence.
    /// </summary>
    public static DateTimeOffset? ToUtc(DateTime siteLocalTime, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(siteLocalTime, DateTimeKind.Unspecified);
        local = local.AddTicks(-(local.Ticks % TimeSpan.TicksPerMillisecond));
        if (zone.IsInvalidTime(local))
        {
            return null;
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
