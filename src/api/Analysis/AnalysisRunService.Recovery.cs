using System.Text.Json;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Data;
using GmailOrganiser.Jobs;
using GmailOrganiser.Llm;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Analysis;

public enum ResumeRunResult
{
    Ok,
    NotFound,

    /// <summary>The run is not failed or stalled (still running, completed or cancelled).</summary>
    Conflict,
}

/// <summary>Start-up recovery and the user's resume of failed or stalled runs (#378).</summary>
public sealed partial class AnalysisRunService
{
    /// <summary>The run's error when its job ended without one, or is gone.</summary>
    public const string JobEndedError = "The job ended without finishing the run.";

    /// <summary>
    /// Runs after <see cref="JobRunner.RecoverAsync"/> has re-queued the interrupted jobs: ends every queued or running
    /// run whose job has finished or is gone, so no run stays "running" with nothing behind it.
    /// </summary>
    public Task RecoverAsync(CancellationToken ct) => SyncEndedJobsAsync(includeMissing: true, ct);

    /// <summary>
    /// Ends the runs whose job ended without the handler recording it (refused by the guard, cancelled while queued or
    /// paused, failed while recording) with the job's end state, so the stored status, the <c>active</c> filter and
    /// the DTO agree. With <paramref name="includeMissing"/> (start-up) a run whose job row is gone fails as well.
    /// </summary>
    private async Task SyncEndedJobsAsync(bool includeMissing, CancellationToken ct)
    {
        var stale = await (
                from run in db.AnalysisRuns.AsNoTracking()
                join job in db.Jobs.AsNoTracking() on run.JobId equals job.Id into jobs
                from job in jobs.DefaultIfEmpty()
                where (run.Status == AnalysisRunStatus.Queued || run.Status == AnalysisRunStatus.Running)
                    && ((job == null && includeMissing) || (job != null && JobRow.Finished.Contains(job.Status)))
                select new
                {
                    run.Id,
                    Status = job == null ? (JobStatus?)null : job.Status,
                    Error = job == null ? null : job.Error,
                    FinishedAt = job == null ? null : job.FinishedAt,
                })
            .ToListAsync(ct);
        foreach (var x in stale)
        {
            var status = x.Status switch
            {
                JobStatus.Completed => AnalysisRunStatus.Completed,
                JobStatus.Cancelled => AnalysisRunStatus.Cancelled,
                _ => AnalysisRunStatus.Failed,
            };
            var error = status == AnalysisRunStatus.Failed ? x.Error ?? JobEndedError : x.Error;
            var finishedAt = x.FinishedAt ?? time.GetUtcNow();
            var ended = await db.AnalysisRuns
                .Where(r => r.Id == x.Id && (r.Status == AnalysisRunStatus.Queued || r.Status == AnalysisRunStatus.Running))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, status)
                    .SetProperty(r => r.Error, r => r.Error ?? error)
                    .SetProperty(r => r.FinishedAt, finishedAt), ct);
            if (ended == 1)
            {
                LogRunEnded(logger, x.Id, SnakeCaseEnumConverter<AnalysisRunStatus>.ToDb(status));
            }
        }
    }

    /// <summary>
    /// Continues a failed or stalled run with a new job: the last stored cursor (rebuilt from the run when its job row
    /// is gone), so groups already stored are skipped, and each failed message not retried before is retried once.
    /// The run is running again, its error cleared.
    /// </summary>
    /// <exception cref="LlmNotConfiguredException">No chat model is selected.</exception>
    public async Task<(ResumeRunResult Result, JobDto? Job)> ResumeAsync(Guid id, CancellationToken ct)
    {
        await SyncEndedJobsAsync(includeMissing: false, ct);
        var found = await WithStalled(db.AnalysisRuns.Where(r => r.Id == id)).SingleOrDefaultAsync(ct);
        if (found is null)
        {
            return (ResumeRunResult.NotFound, null);
        }

        var run = found.Run;
        if (run.Status != AnalysisRunStatus.Failed && !found.Stalled)
        {
            return (ResumeRunResult.Conflict, null);
        }

        var settings = await RequireChatModelAsync(ct);
        var stored = run.JobId is { } jobId
            ? await db.Jobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.Cursor).SingleOrDefaultAsync(ct)
            : null;
        var cursor = stored is null ? null : JsonSerializer.Deserialize<AnalysisRunCursor>(stored, JobRow.Json);
        if (cursor is null)
        {
            cursor = await RebuildCursorAsync(run, settings, ct);
        }
        else
        {
            var retried = (cursor.RetriedIds ?? []).ToHashSet(StringComparer.Ordinal);
            var failed = cursor.FailedIds ?? [];
            var retry = failed.Where(f => !retried.Contains(f)).ToArray();
            cursor = cursor with { FailedIds = [.. failed.Where(retried.Contains)], RetriedIds = [.. retried, .. retry] };
            run.FailedMessages = Math.Max(0, run.FailedMessages - retry.Length);
        }

        run.Status = AnalysisRunStatus.Running;
        run.Error = null;
        run.FinishedAt = null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (job, _) = await jobs.EnqueueAsync(AnalysisRunJob.JobType, AnalysisRunJob.Queue, cursor, ct, dedupKey: run.Id.ToString());
        run.JobId = job.Id;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (ResumeRunResult.Ok, job);
    }

    /// <summary>
    /// The cursor of a run whose job row is gone. Which messages failed is lost with it; they are still not analysed,
    /// so they come back as candidates and the failed count starts over. An analyse run takes its ids again (messages
    /// scope) or the scope's next candidates for the emails it still owes; a compare run re-freezes its messages'
    /// suggestions and counts those it wrote an alternative for as covered.
    /// </summary>
    private async Task<AnalysisRunCursor> RebuildCursorAsync(AnalysisRunRow run, AppSettings settings, CancellationToken ct)
    {
        run.FailedMessages = 0;
        if (run.Kind == AnalysisRunKind.Compare)
        {
            var ids = run.MessageIds ?? [];
            var suggestions = await db.Suggestions.AsNoTracking()
                .Where(s => ids.Contains(s.MessageId))
                .ToDictionaryAsync(s => s.MessageId, s => s.Id, StringComparer.Ordinal, ct);
            var covered = await db.SuggestionAlternatives.AsNoTracking()
                .Where(a => a.RunId == run.Id)
                .Select(a => a.MessageId)
                .ToListAsync(ct);
            var candidates = await db.Messages.AsNoTracking()
                .Where(m => ids.Contains(m.Id) && !m.DeletedInGmail)
                .OrderByDescending(m => m.InternalDate)
                .ThenBy(m => m.Id)
                .Select(m => m.Id)
                .ToListAsync(ct);
            return new AnalysisRunCursor(
                run.Id, CandidateIds: [.. candidates.Where(suggestions.ContainsKey)], SuggestionIds: suggestions, CoveredIds: covered);
        }

        var count = run.Scope == AnalysisScope.Messages ? run.MessageIds?.Length ?? 0 : run.RequestedCount - run.MessagesCovered;
        if (count < 1)
        {
            return new AnalysisRunCursor(run.Id, CandidateIds: []);
        }

        var appLabelIds = run.Scope == AnalysisScope.Labelled ? (await PersonalLabels.LoadAsync(labelCatalog, settings, ct)).AppLabelIds : [];
        var rows = await AnalysisCandidates.QueryAsync(db, run.Scope, run.SenderAddress, run.MessageIds, count, appLabelIds, ct);
        return new AnalysisRunCursor(run.Id, CandidateIds: [.. rows.Select(m => m.Id)]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis run {RunId} ended as {Status}: its job had finished or was gone")]
    private static partial void LogRunEnded(ILogger logger, Guid runId, string status);
}
