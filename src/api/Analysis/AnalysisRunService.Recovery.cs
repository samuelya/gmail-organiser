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

    /// <summary>
    /// The run is not failed or stalled (still running, completed or cancelled), or its job is active again.
    /// </summary>
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
    public Task RecoverAsync(CancellationToken ct) => SyncEndedJobsAsync(ct);

    /// <summary>
    /// Ends the runs whose job ended without the handler recording it (refused by the guard, cancelled while queued or
    /// paused, failed while recording) with the job's end state, so the stored status, the <c>active</c> filter and
    /// the DTO agree. A run whose job row is gone fails: a run and its job are written in one transaction, so a
    /// missing row never means "not started yet".
    /// </summary>
    private async Task SyncEndedJobsAsync(CancellationToken ct)
    {
        var stale = await (
                from run in db.AnalysisRuns.AsNoTracking()
                join job in db.Jobs.AsNoTracking() on run.JobId equals job.Id into jobs
                from job in jobs.DefaultIfEmpty()
                where (run.Status == AnalysisRunStatus.Queued || run.Status == AnalysisRunStatus.Running)
                    && (job == null || JobRow.Finished.Contains(job.Status))
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
    /// Continues a failed or stalled run from its last stored cursor (rebuilt from the run when its job row is gone),
    /// so groups already stored are skipped, and retries each failed message not retried before once. A failed job is
    /// re-queued with the new cursor, so the run keeps one job; a new job is made only when the old one can't be
    /// resumed. The run is queued again, its error cleared; the handler sets it running.
    /// </summary>
    /// <exception cref="LlmNotConfiguredException">No chat model is selected.</exception>
    public async Task<(ResumeRunResult Result, JobDto? Job)> ResumeAsync(Guid id, CancellationToken ct)
    {
        await SyncEndedJobsAsync(ct);
        var found = await (
                // Tracks the run (AsNoTracking anywhere in the query would apply to all of it); the job is read as values.
                from r in db.AnalysisRuns
                where r.Id == id
                join j in db.Jobs on r.JobId equals j.Id into js
                from j in js.DefaultIfEmpty()
                select new
                {
                    Run = r,
                    JobStatus = j == null ? (JobStatus?)null : j.Status,
                    Cursor = j == null ? null : j.Cursor,
                    Version = j == null ? 0 : j.Version,
                })
            .SingleOrDefaultAsync(ct);
        if (found is null)
        {
            return (ResumeRunResult.NotFound, null);
        }

        var run = found.Run;
        var active = found.JobStatus is { } status && JobRow.Active.Contains(status);
        var stalled = run.Status is AnalysisRunStatus.Queued or AnalysisRunStatus.Running && !active;
        if (active || (run.Status != AnalysisRunStatus.Failed && !stalled))
        {
            return (ResumeRunResult.Conflict, null);
        }

        var settings = await RequireChatModelAsync(ct);
        var cursor = found.Cursor is null ? null : JsonSerializer.Deserialize<AnalysisRunCursor>(found.Cursor, JobRow.Json);
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

        run.Status = AnalysisRunStatus.Queued;
        run.Error = null;
        run.FinishedAt = null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var job = found.JobStatus == JobStatus.Failed
            ? await RequeueAsync(run.JobId!.Value, found.Version, cursor, ct)
            : await EnqueueNewAsync(run.Id, cursor, ct);
        if (job is null)
        {
            // The job moved on meanwhile (resumed from the Jobs API, or a concurrent resume won): nothing changes.
            return (ResumeRunResult.Conflict, null);
        }

        run.JobId = job.Id;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (ResumeRunResult.Ok, job);
    }

    /// <summary>The failed job with <paramref name="cursor"/>, queued again; null when it changed since it was read.</summary>
    private async Task<JobDto?> RequeueAsync(Guid jobId, long version, AnalysisRunCursor cursor, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(cursor, JobRow.Json);
        var now = time.GetUtcNow();
        var written = await db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Failed && j.Version == version)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Cursor, json).Touch(now), ct);
        return written == 1 && await jobs.ResumeAsync(jobId, ct) == JobActionResult.Ok ? await jobs.GetAsync(jobId, ct) : null;
    }

    /// <summary>A new job for a run whose job row is gone or ended completed / cancelled; null when one is active.</summary>
    private async Task<JobDto?> EnqueueNewAsync(Guid runId, AnalysisRunCursor cursor, CancellationToken ct)
    {
        var (job, created) = await jobs.EnqueueAsync(AnalysisRunJob.JobType, AnalysisRunJob.Queue, cursor, ct, dedupKey: runId.ToString());
        return created ? job : null;
    }

    /// <summary>
    /// The cursor of a run whose job row is gone. Which messages failed is lost with it; they are still not analysed,
    /// so they come back as candidates and the failed count starts over. The candidates start with the messages the
    /// run already covered (the job skips them; they keep the progress total right), then the ones it still owes;
    /// the skipped count is set so covered + skipped + owed adds up as at the start.
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
            // A message whose suggestion is gone (or that is deleted) is skipped, counted once here.
            string[] kept = [.. candidates.Where(c => suggestions.ContainsKey(c) || covered.Contains(c))];
            run.SkippedMessages = Math.Max(0, run.RequestedCount - kept.Length);
            return new AnalysisRunCursor(run.Id, CandidateIds: kept, SuggestionIds: suggestions, CoveredIds: covered);
        }

        var stored = await (
                from s in db.Suggestions.AsNoTracking()
                where s.RunId == run.Id
                join m in db.Messages.AsNoTracking() on s.MessageId equals m.Id
                orderby m.InternalDate descending, m.Id
                select m.Id)
            .ToListAsync(ct);
        var messages = run.Scope == AnalysisScope.Messages;
        var owed = messages ? run.MessageIds?.Length ?? 0 : run.RequestedCount - run.MessagesCovered - run.SkippedMessages;
        if (owed < 1)
        {
            return new AnalysisRunCursor(run.Id, CandidateIds: stored);
        }

        var appLabelIds = run.Scope == AnalysisScope.Labelled ? (await PersonalLabels.LoadAsync(labelCatalog, settings, ct)).AppLabelIds : [];
        var rows = await AnalysisCandidates.QueryAsync(db, run.Scope, run.SenderAddress, run.MessageIds, owed, appLabelIds, ct);
        string[] open = [.. rows.Select(m => m.Id).Except(stored, StringComparer.Ordinal)];
        if (messages)
        {
            // The explicit ids the run neither covered nor still owes (decided or deleted meanwhile) are skipped.
            run.SkippedMessages = Math.Max(0, run.RequestedCount - run.MessagesCovered - open.Length);
        }

        return new AnalysisRunCursor(run.Id, CandidateIds: [.. stored, .. open]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis run {RunId} ended as {Status}: its job had finished or was gone")]
    private static partial void LogRunEnded(ILogger logger, Guid runId, string status);
}
