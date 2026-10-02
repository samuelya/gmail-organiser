using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Llm;
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
    TimeProvider time)
{
    public const int MaxListLimit = 200;

    /// <summary>Stores a queued run and enqueues its job (one per run; the analysis queue runs them one at a time).</summary>
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
        var run = new AnalysisRunRow
        {
            Id = Guid.CreateVersion7(now),
            Scope = scope,
            SenderAddress = scope == AnalysisScope.Sender ? senderAddress?.Trim().ToLowerInvariant() : null,
            MessageIds = scope == AnalysisScope.Messages ? messageIds?.Distinct(StringComparer.Ordinal).ToArray() : null,
            RequestedCount = count,
            GroupingMode = groupingMode ?? settings.AnalysisGroupingMode,
            Status = AnalysisRunStatus.Queued,
            CreatedAt = now,
        };
        db.AnalysisRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var (job, _) = await jobs.EnqueueAsync(
            AnalysisRunJob.JobType, AnalysisRunJob.Queue, new AnalysisRunCursor(run.Id), ct, dedupKey: run.Id.ToString());
        run.JobId = job.Id;
        await db.SaveChangesAsync(ct);
        return ToDto(run, null);
    }

    public async Task<IReadOnlyList<AnalysisRunDto>> ListAsync(bool? active, int limit, CancellationToken ct)
    {
        var query = WithJobs();
        if (active is { } a)
        {
            query = a
                ? query.Where(x => x.Run.Status == AnalysisRunStatus.Queued || x.Run.Status == AnalysisRunStatus.Running)
                : query.Where(x => x.Run.Status != AnalysisRunStatus.Queued && x.Run.Status != AnalysisRunStatus.Running);
        }

        var rows = await query.OrderByDescending(x => x.Run.CreatedAt).Take(Math.Clamp(limit, 1, MaxListLimit)).ToListAsync(ct);
        return [.. rows.Select(x => ToDto(x.Run, x.Job))];
    }

    public async Task<AnalysisRunDto?> GetAsync(Guid id, CancellationToken ct) =>
        await WithJobs().Where(x => x.Run.Id == id).FirstOrDefaultAsync(ct) is { } x ? ToDto(x.Run, x.Job) : null;

    /// <summary>
    /// Asks the run's job to stop after its current group; a queued run ends at once. Null when the run does not exist,
    /// <see cref="JobActionResult.Conflict"/> when it has already finished.
    /// </summary>
    public async Task<(JobActionResult Result, AnalysisRunDto? Run)> CancelAsync(Guid id, CancellationToken ct)
    {
        var run = await db.AnalysisRuns.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (run is null)
        {
            return (JobActionResult.NotFound, null);
        }

        var result = run.JobId is { } jobId ? await jobs.CancelAsync(jobId, ct) : JobActionResult.NotFound;
        if (result == JobActionResult.Ok
            && await db.Jobs.AsNoTracking().AnyAsync(j => j.Id == run.JobId && j.Status == JobStatus.Cancelled, ct))
        {
            // The job never ran (or was paused or failed): nothing else will end the run.
            await db.AnalysisRuns
                .Where(r => r.Id == id && (r.Status == AnalysisRunStatus.Queued || r.Status == AnalysisRunStatus.Running))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, AnalysisRunStatus.Cancelled)
                    .SetProperty(r => r.FinishedAt, time.GetUtcNow()), ct);
        }

        if (result == JobActionResult.NotFound)
        {
            result = JobActionResult.Conflict;
        }

        return (result, await GetAsync(id, ct));
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
        var counts = await db.Messages.AsNoTracking()
            .Where(m => !m.DeletedInGmail)
            .GroupBy(m => m.AnalysisStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);
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
            SavedPercent(llmCalls, covered));
    }

    /// <summary><c>1 − llmCalls / max(1, covered)</c>; negative when retries cost more calls than emails covered.</summary>
    public static double SavedPercent(long llmCalls, long covered) => 1 - ((double)llmCalls / Math.Max(1, covered));

    private IQueryable<RunWithJob> WithJobs() =>
        from run in db.AnalysisRuns.AsNoTracking()
        join job in db.Jobs.AsNoTracking() on run.JobId equals job.Id into jobsOfRun
        from job in jobsOfRun.DefaultIfEmpty()
        select new RunWithJob(run, job);

    private sealed record RunWithJob(AnalysisRunRow Run, JobRow? Job);

    /// <summary>
    /// The run as stored, except while it says queued or running but its job has already ended (refused before the
    /// handler ran, cancelled while queued, or failed while recording): then the job's end state is reported.
    /// </summary>
    private static AnalysisRunDto ToDto(AnalysisRunRow run, JobRow? job)
    {
        var status = run.Status;
        var error = run.Error;
        if (status is AnalysisRunStatus.Queued or AnalysisRunStatus.Running && job is { Status: var js } && JobRow.Finished.Contains(js))
        {
            status = js switch
            {
                JobStatus.Completed => AnalysisRunStatus.Completed,
                JobStatus.Cancelled => AnalysisRunStatus.Cancelled,
                _ => AnalysisRunStatus.Failed,
            };
            error ??= job.Error;
        }

        return new AnalysisRunDto(
            run.Id,
            run.JobId,
            SnakeCaseEnumConverter<AnalysisScope>.ToDb(run.Scope),
            run.SenderAddress,
            run.RequestedCount,
            SnakeCaseEnumConverter<AnalysisGroupingMode>.ToDb(run.GroupingMode),
            SnakeCaseEnumConverter<AnalysisRunStatus>.ToDb(status),
            run.MessagesCovered,
            run.MessagesLlm,
            run.MessagesDerived,
            run.MessagesFromMemory,
            run.LlmCalls,
            run.Groups,
            run.MixedGroups,
            run.FailedMessages,
            run.Model,
            run.PromptVersion,
            error,
            SavedPercent(run.LlmCalls, run.MessagesCovered),
            run.CreatedAt,
            run.StartedAt,
            run.FinishedAt ?? (status != run.Status ? job?.FinishedAt : null));
    }
}
