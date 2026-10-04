using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Llm;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Analysis;

public enum ReanalyseResult
{
    Ok,

    /// <summary>Every matching suggestion is approved or applied, so nothing was reset.</summary>
    OnlyDecided,
}

/// <summary>Creates, lists and cancels analysis runs and resets messages for re-analysis. Endpoints validate first.</summary>
public sealed class AnalysisRunService(
    AppDbContext db,
    IJobService jobs,
    ISettingsStore settingsStore,
    SenderStatsUpdater senderStats,
    LabelCatalog labelCatalog,
    TimeProvider time)
{
    public const int MaxListLimit = 200;

    /// <summary>
    /// Freezes the run's candidates, then stores a queued run and enqueues its job (one per run; the analysis queue
    /// runs them one at a time) in one transaction, so a failed enqueue leaves no run behind.
    /// </summary>
    /// <exception cref="LlmNotConfiguredException">No chat model is selected.</exception>
    public async Task<AnalysisRunDto> StartAsync(
        AnalysisScope scope, string? senderAddress, string[]? messageIds, int count, AnalysisGroupingMode? groupingMode,
        CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.ChatModel))
        {
            throw new LlmNotConfiguredException(ModelKinds.Chat);
        }

        var now = time.GetUtcNow();
        var sender = scope == AnalysisScope.Sender ? senderAddress?.Trim().ToLowerInvariant() : null;
        var ids = scope == AnalysisScope.Messages ? messageIds?.Distinct(StringComparer.Ordinal).ToArray() : null;

        // A resume works over exactly these ids, whatever is fetched meanwhile.
        // Without Gmail, app labels count as personal here; the run's eligibility check skips such candidates.
        var appLabelIds = scope == AnalysisScope.Labelled ? (await PersonalLabels.LoadAsync(labelCatalog, settings, ct)).AppLabelIds : [];
        var candidates = await AnalysisCandidates.QueryAsync(db, scope, sender, ids, count, appLabelIds, ct);
        var run = new AnalysisRunRow
        {
            Id = Guid.CreateVersion7(now),
            Scope = scope,
            SenderAddress = sender,
            MessageIds = ids,
            RequestedCount = count,
            GroupingMode = groupingMode ?? settings.AnalysisGroupingMode,
            Status = AnalysisRunStatus.Queued,
            SkippedMessages = AnalysisCandidates.Skipped(scope, count, candidates.Count),
            CreatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.AnalysisRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var cursor = new AnalysisRunCursor(run.Id, CandidateIds: [.. candidates.Select(m => m.Id)]);
        var (job, _) = await jobs.EnqueueAsync(AnalysisRunJob.JobType, AnalysisRunJob.Queue, cursor, ct, dedupKey: run.Id.ToString());
        run.JobId = job.Id;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(run);
    }

    public async Task<IReadOnlyList<AnalysisRunDto>> ListAsync(bool? active, int limit, CancellationToken ct)
    {
        await SyncEndedJobsAsync(ct);
        var query = db.AnalysisRuns.AsNoTracking();
        if (active is { } a)
        {
            query = a
                ? query.Where(r => r.Status == AnalysisRunStatus.Queued || r.Status == AnalysisRunStatus.Running)
                : query.Where(r => r.Status != AnalysisRunStatus.Queued && r.Status != AnalysisRunStatus.Running);
        }

        var rows = await query.OrderByDescending(r => r.CreatedAt).Take(Math.Clamp(limit, 1, MaxListLimit)).ToListAsync(ct);
        return [.. rows.Select(ToDto)];
    }

    public async Task<AnalysisRunDto?> GetAsync(Guid id, CancellationToken ct)
    {
        await SyncEndedJobsAsync(ct);
        return await db.AnalysisRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) is { } run ? ToDto(run) : null;
    }

    /// <summary>
    /// Asks the run's job to stop after its current group; a queued or paused run ends at once. Allowed only while the
    /// job is queued, running or paused: otherwise <see cref="JobActionResult.Conflict"/> and nothing changes.
    /// </summary>
    public async Task<(JobActionResult Result, AnalysisRunDto? Run)> CancelAsync(Guid id, CancellationToken ct)
    {
        var run = await db.AnalysisRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (run is null)
        {
            return (JobActionResult.NotFound, null);
        }

        var jobActive = run.JobId is { } jobId
            && await db.Jobs.AsNoTracking().AnyAsync(j => j.Id == jobId && JobRow.Active.Contains(j.Status), ct);
        if (!jobActive || run.Status is AnalysisRunStatus.Completed or AnalysisRunStatus.Cancelled)
        {
            return (JobActionResult.Conflict, await GetAsync(id, ct));
        }

        var result = await jobs.CancelAsync(run.JobId!.Value, ct);
        if (result == JobActionResult.NotFound)
        {
            result = JobActionResult.Conflict;
        }

        // A queued or paused job is cancelled at once and no handler will end the run; GetAsync syncs it.
        return (result, await GetAsync(id, ct));
    }

    /// <summary>
    /// Ends the runs whose job ended without the handler recording it (refused by the guard, cancelled while queued or
    /// paused, failed while recording) with the job's end state, so the stored status, the <c>active</c> filter and
    /// the DTO agree.
    /// </summary>
    private async Task SyncEndedJobsAsync(CancellationToken ct)
    {
        var stale = await (
                from run in db.AnalysisRuns.AsNoTracking()
                join job in db.Jobs.AsNoTracking() on run.JobId equals job.Id
                where (run.Status == AnalysisRunStatus.Queued || run.Status == AnalysisRunStatus.Running)
                    && JobRow.Finished.Contains(job.Status)
                select new { run.Id, job.Status, job.Error, job.FinishedAt })
            .ToListAsync(ct);
        foreach (var x in stale)
        {
            var status = x.Status switch
            {
                JobStatus.Completed => AnalysisRunStatus.Completed,
                JobStatus.Cancelled => AnalysisRunStatus.Cancelled,
                _ => AnalysisRunStatus.Failed,
            };
            var finishedAt = x.FinishedAt ?? time.GetUtcNow();
            await db.AnalysisRuns
                .Where(r => r.Id == x.Id && (r.Status == AnalysisRunStatus.Queued || r.Status == AnalysisRunStatus.Running))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, status)
                    .SetProperty(r => r.Error, r => r.Error ?? x.Error)
                    .SetProperty(r => r.FinishedAt, finishedAt), ct);
        }
    }

    /// <summary>
    /// Deletes the pending and rejected suggestions of the given messages (or of every message of a sender) and sets
    /// those messages back to not analysed; approved and applied ones are kept.
    /// </summary>
    public async Task<(ReanalyseResult Result, int Reset)> ReanalyseAsync(string[]? messageIds, string? senderAddress, CancellationToken ct)
    {
        var suggestions = db.Suggestions.AsQueryable();
        if (messageIds is { Length: > 0 })
        {
            var ids = messageIds.Distinct(StringComparer.Ordinal).ToArray();
            suggestions = suggestions.Where(s => ids.Contains(s.MessageId));
        }
        else
        {
            var address = senderAddress!.Trim().ToLowerInvariant();
            suggestions = suggestions.Where(s => db.Messages.Any(m => m.Id == s.MessageId && m.FromAddress == address));
        }

        var matched = await suggestions.Select(s => new { s.MessageId, s.Status }).ToListAsync(ct);
        var resettable = matched
            .Where(s => s.Status is SuggestionStatus.Pending or SuggestionStatus.Rejected)
            .Select(s => s.MessageId)
            .ToArray();
        if (resettable.Length == 0)
        {
            return (matched.Count > 0 ? ReanalyseResult.OnlyDecided : ReanalyseResult.Ok, 0);
        }

        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var deleted = await db.Suggestions
            .Where(s => resettable.Contains(s.MessageId)
                && (s.Status == SuggestionStatus.Pending || s.Status == SuggestionStatus.Rejected))
            .ExecuteDeleteAsync(ct);
        await db.Messages
            .Where(m => resettable.Contains(m.Id) && !db.Suggestions.Any(s => s.MessageId == m.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.AnalysisStatus, AnalysisStatus.NotAnalysed)
                .SetProperty(m => m.UpdatedAt, now), ct);
        var addresses = await db.Messages.Where(m => resettable.Contains(m.Id)).Select(m => m.FromAddress).Distinct().ToListAsync(ct);
        await senderStats.UpdateAnalysedCountsAsync(addresses, ct);
        await tx.CommitAsync(ct);
        return (ReanalyseResult.Ok, deleted);
    }

    public async Task<AnalysisSummaryDto> SummaryAsync(CancellationToken ct)
    {
        var labels = await PersonalLabels.LoadAsync(labelCatalog, await settingsStore.GetAsync(ct), ct);
        var (counts, labelled) = await AnalysisCandidates.CountAsync(db, labels.AppLabelIds, ct);
        var applied = db.Suggestions.AsNoTracking().Where(s => s.Status == SuggestionStatus.Applied);
        var actionCount = await applied.CountAsync(s => s.NeedsAction, ct);
        var deleteCount = await applied.CountAsync(s => s.ToBeDeleted, ct);
        var totals = await db.AnalysisRuns.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new { LlmCalls = g.Sum(r => (long)r.LlmCalls), Covered = g.Sum(r => (long)r.MessagesCovered) })
            .FirstOrDefaultAsync(ct);
        var llmCalls = totals?.LlmCalls ?? 0;
        var covered = totals?.Covered ?? 0;

        return new AnalysisSummaryDto(
            counts.GetValueOrDefault(AnalysisStatus.NotAnalysed),
            counts.GetValueOrDefault(AnalysisStatus.Analysed),
            counts.GetValueOrDefault(AnalysisStatus.Approved),
            counts.GetValueOrDefault(AnalysisStatus.Rejected),
            counts.GetValueOrDefault(AnalysisStatus.Applied),
            actionCount,
            deleteCount,
            llmCalls,
            covered,
            SavedPercent(llmCalls, covered),
            labelled);
    }

    /// <summary><c>1 − llmCalls / max(1, covered)</c>; negative when retries cost more calls than emails covered.</summary>
    public static double SavedPercent(long llmCalls, long covered) => 1 - ((double)llmCalls / Math.Max(1, covered));

    private static AnalysisRunDto ToDto(AnalysisRunRow run) => new(
        run.Id,
        run.JobId,
        SnakeCaseEnumConverter<AnalysisScope>.ToDb(run.Scope),
        run.SenderAddress,
        run.RequestedCount,
        SnakeCaseEnumConverter<AnalysisGroupingMode>.ToDb(run.GroupingMode),
        SnakeCaseEnumConverter<AnalysisRunStatus>.ToDb(run.Status),
        run.MessagesCovered,
        run.MessagesLlm,
        run.MessagesDerived,
        run.MessagesFromMemory,
        run.LlmCalls,
        run.Groups,
        run.MixedGroups,
        run.FailedMessages,
        run.SkippedMessages,
        run.AttachmentsConverted,
        run.AttachmentsSkipped,
        run.Model,
        run.PromptVersion,
        run.Error,
        SavedPercent(run.LlmCalls, run.MessagesCovered),
        run.CreatedAt,
        run.StartedAt,
        run.FinishedAt);
}
