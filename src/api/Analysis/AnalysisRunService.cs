using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Policies;
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

public enum CompareRunResult
{
    Ok,
    RunNotFound,

    /// <summary>The ids or the run name no suggestion.</summary>
    Empty,

    /// <summary>More than <see cref="SettingsValidation.MaxAnalysisDefaultCount"/> suggestions.</summary>
    TooMany,
}

/// <summary>
/// Creates, lists, cancels, recovers and resumes analysis runs and resets messages for re-analysis. Endpoints validate
/// first.
/// </summary>
public sealed partial class AnalysisRunService(
    AppDbContext db,
    IJobService jobs,
    ISettingsStore settingsStore,
    ClaudeApiKeyService claudeApiKey,
    SenderStatsUpdater senderStats,
    LabelCatalog labelCatalog,
    TimeProvider time,
    ILogger<AnalysisRunService> logger) : IJobStartupRecovery
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
        var settings = await RequireChatModelAsync(ct);

        var now = time.GetUtcNow();
        var sender = scope is AnalysisScope.Sender or AnalysisScope.TopSenders ? senderAddress?.Trim().ToLowerInvariant() : null;
        var ids = scope == AnalysisScope.Messages ? messageIds?.Distinct(StringComparer.Ordinal).ToArray() : null;
        if (scope == AnalysisScope.TopSenders)
        {
            // The senders are frozen like a message run's candidates; the job walks them by index.
            var senders = await PolicyCandidates.QueryAsync(db, sender, count, settings.AnalysisMinGroupSize, ct);
            var policyRun = NewRun(scope, sender, null, count, groupingMode ?? settings.AnalysisGroupingMode, now);
            await EnqueueAsync(policyRun, new AnalysisRunCursor(policyRun.Id, Senders: senders), ct);
            return ToDto(policyRun);
        }

        // A resume works over exactly these ids, whatever is fetched meanwhile.
        // Without Gmail, app labels count as personal here; the run's eligibility check skips such candidates.
        var appLabelIds = scope == AnalysisScope.Labelled ? (await PersonalLabels.LoadAsync(labelCatalog, settings, ct)).AppLabelIds : [];
        var candidates = await AnalysisCandidates.QueryAsync(db, scope, sender, ids, count, appLabelIds, ct);
        var run = NewRun(scope, sender, ids, count, groupingMode ?? settings.AnalysisGroupingMode, now);
        run.SkippedMessages = AnalysisCandidates.Skipped(scope, count, candidates.Count);
        await EnqueueAsync(run, new AnalysisRunCursor(run.Id, CandidateIds: [.. candidates.Select(m => m.Id)]), ct);
        return ToDto(run);
    }

    private static AnalysisRunRow NewRun(
        AnalysisScope scope, string? sender, string[]? ids, int count, AnalysisGroupingMode groupingMode, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            Scope = scope,
            SenderAddress = sender,
            MessageIds = ids,
            RequestedCount = count,
            GroupingMode = groupingMode,
            Status = AnalysisRunStatus.Queued,
            CreatedAt = now,
        };

    /// <summary>
    /// Starts a compare run (#248) over suggestions of any status: <paramref name="suggestionIds"/>, or every suggestion
    /// <paramref name="runId"/> wrote (or wrote an alternative for). Freezes each message's suggestion; messages deleted
    /// in Gmail and unknown ids count as skipped. Queued like any run, so it never overlaps another analysis run.
    /// </summary>
    /// <exception cref="LlmNotConfiguredException">No chat model is selected.</exception>
    public async Task<(CompareRunResult Result, AnalysisRunDto? Run)> StartCompareAsync(
        Guid[]? suggestionIds, Guid? runId, CancellationToken ct)
    {
        var settings = await RequireChatModelAsync(ct);

        var max = SettingsValidation.MaxAnalysisDefaultCount;
        var suggestions = db.Suggestions.AsNoTracking();
        int? requestedIds = null;
        if (runId is { } id)
        {
            if (!await db.AnalysisRuns.AnyAsync(r => r.Id == id, ct))
            {
                return (CompareRunResult.RunNotFound, null);
            }

            var compared = db.SuggestionAlternatives.Where(a => a.RunId == id).Select(a => a.SuggestionId);
            suggestions = suggestions.Where(s => s.RunId == id || compared.Contains(s.Id)).OrderBy(s => s.Id).Take(max + 1);
        }
        else
        {
            var ids = (suggestionIds ?? []).Distinct().ToArray();
            if (ids.Length > max)
            {
                return (CompareRunResult.TooMany, null);
            }

            suggestions = suggestions.Where(s => ids.Contains(s.Id));
            requestedIds = ids.Length;
        }

        // One read: the run's count and its frozen list cannot disagree.
        var frozen = await suggestions.Select(s => new { s.Id, s.MessageId }).ToListAsync(ct);
        if (frozen.Count > max)
        {
            return (CompareRunResult.TooMany, null);
        }

        var requested = requestedIds ?? frozen.Count;
        if (frozen.Count == 0)
        {
            return (CompareRunResult.Empty, null);
        }

        var messageIds = frozen.Select(s => s.MessageId).ToArray();
        var candidates = await db.Messages.AsNoTracking()
            .Where(m => messageIds.Contains(m.Id) && !m.DeletedInGmail)
            .OrderByDescending(m => m.InternalDate)
            .ThenBy(m => m.Id)
            .Select(m => m.Id)
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        var run = new AnalysisRunRow
        {
            Id = Guid.CreateVersion7(now),
            Kind = AnalysisRunKind.Compare,
            Scope = AnalysisScope.Messages,
            MessageIds = messageIds,
            RequestedCount = requested,
            GroupingMode = settings.AnalysisGroupingMode,
            Status = AnalysisRunStatus.Queued,
            SkippedMessages = requested - candidates.Count,
            CreatedAt = now,
        };

        var cursor = new AnalysisRunCursor(
            run.Id, CandidateIds: candidates, SuggestionIds: frozen.ToDictionary(s => s.MessageId, s => s.Id, StringComparer.Ordinal));
        await EnqueueAsync(run, cursor, ct);
        return (CompareRunResult.Ok, ToDto(run));
    }

    /// <summary>The settings, once the active provider can chat; shared by the run starters so they cannot drift apart.</summary>
    /// <exception cref="LlmNotConfiguredException">No chat model is selected, or no usable Claude API key is set.</exception>
    private Task<AppSettings> RequireChatModelAsync(CancellationToken ct) => ActiveChat.RequireAsync(settingsStore, claudeApiKey, ct);

    /// <summary>Stores the queued run and enqueues its job in one transaction, so a failed enqueue leaves no run behind.</summary>
    private async Task EnqueueAsync(AnalysisRunRow run, AnalysisRunCursor cursor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.AnalysisRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var (job, _) = await jobs.EnqueueAsync(AnalysisRunJob.JobType, AnalysisRunJob.Queue, cursor, ct, dedupKey: run.Id.ToString());
        run.JobId = job.Id;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<AnalysisRunDto>> ListAsync(bool? active, AnalysisRunStatus? status, int limit, CancellationToken ct)
    {
        await SyncEndedJobsAsync(ct);
        var query = db.AnalysisRuns.AsNoTracking();
        if (active is { } a)
        {
            query = a
                ? query.Where(r => r.Status == AnalysisRunStatus.Queued || r.Status == AnalysisRunStatus.Running)
                : query.Where(r => r.Status != AnalysisRunStatus.Queued && r.Status != AnalysisRunStatus.Running);
        }

        if (status is { } s)
        {
            query = query.Where(r => r.Status == s);
        }

        var rows = await WithStalled(query)
            .OrderByDescending(x => x.Run.CreatedAt)
            .Take(Math.Clamp(limit, 1, MaxListLimit))
            .ToListAsync(ct);
        return [.. rows.Select(x => ToDto(x.Run, x.Stalled, x.NewLabels))];
    }

    public async Task<AnalysisRunDto?> GetAsync(Guid id, CancellationToken ct)
    {
        await SyncEndedJobsAsync(ct);
        return await WithStalled(db.AnalysisRuns.AsNoTracking().Where(r => r.Id == id)).SingleOrDefaultAsync(ct) is { } x
            ? ToDto(x.Run, x.Stalled, x.NewLabels)
            : null;
    }

    private sealed class RunWithStall
    {
        public required AnalysisRunRow Run { get; init; }
        public bool Stalled { get; init; }
        public int NewLabels { get; init; }
    }

    /// <summary>Pairs each run with whether it is queued or running while no active job is behind it.</summary>
    private IQueryable<RunWithStall> WithStalled(IQueryable<AnalysisRunRow> runs) =>
        from run in runs
        join job in db.Jobs.AsNoTracking() on run.JobId equals job.Id into jobs
        from job in jobs.DefaultIfEmpty()
        select new RunWithStall
        {
            Run = run,
            Stalled = (run.Status == AnalysisRunStatus.Queued || run.Status == AnalysisRunStatus.Running)
                && (job == null || !JobRow.Active.Contains(job.Status)),
            NewLabels = db.Suggestions.Where(s => s.RunId == run.Id && s.IsNewLabel).Select(s => s.TopicLabel).Distinct().Count(),
        };

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
    /// Deletes the pending and rejected suggestions of the given messages (or of every message of a sender) and sets
    /// those messages back to not analysed; approved and applied ones are kept. A deleted suggestion's compare-run
    /// alternative goes with it (cascade), unseen.
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
        // Compare runs re-cover analysed mail without the short-circuit; they would inflate covered and lower saved %.
        var totals = await db.AnalysisRuns.AsNoTracking()
            .Where(r => r.Kind == AnalysisRunKind.Analyse)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                LlmCalls = g.Sum(r => (long)r.LlmCalls),
                Covered = g.Sum(r => (long)r.MessagesCovered),
                Policies = g.Sum(r => (long)r.PoliciesProposed),
                PromptTokens = g.Sum(r => r.PromptTokens),
                CompletionTokens = g.Sum(r => r.CompletionTokens),
                LlmMilliseconds = g.Sum(r => r.LlmMilliseconds),
                Triage = g.Sum(r => (long)r.TriageCalls),
                Escalated = g.Sum(r => (long)r.EscalatedCalls),
                Packed = g.Sum(r => (long)r.PackedMessages),
                PackRetries = g.Sum(r => (long)r.PackRetries),
            })
            .FirstOrDefaultAsync(ct);
        var llmCalls = totals?.LlmCalls ?? 0;
        var covered = totals?.Covered ?? 0;
        var alternatives = await db.SuggestionAlternatives.CountAsync(ct);

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
            labelled,
            alternatives,
            totals?.Policies ?? 0,
            totals?.PromptTokens ?? 0,
            totals?.CompletionTokens ?? 0,
            (totals?.LlmMilliseconds ?? 0) / 1000.0,
            totals?.Triage ?? 0,
            totals?.Escalated ?? 0,
            totals?.Packed ?? 0,
            totals?.PackRetries ?? 0);
    }

    /// <summary><c>1 − llmCalls / max(1, covered)</c>; negative when retries cost more calls than emails covered.</summary>
    public static double SavedPercent(long llmCalls, long covered) => 1 - ((double)llmCalls / Math.Max(1, covered));

    private static AnalysisRunDto ToDto(AnalysisRunRow run, bool stalled = false, int newLabels = 0) => new(
        run.Id,
        run.JobId,
        SnakeCaseEnumConverter<AnalysisRunKind>.ToDb(run.Kind),
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
        run.FinishedAt,
        run.PromptTokens,
        run.CompletionTokens,
        run.LlmMilliseconds,
        run.LlmMilliseconds / 1000.0,
        run.NearContextLimit,
        run.TriageCalls,
        run.EscalatedCalls,
        stalled,
        run.PoliciesProposed,
        newLabels,
        run.PackedMessages,
        run.PackRetries,
        run.EmbeddingFallback);
}
