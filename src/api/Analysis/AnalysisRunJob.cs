using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Llm;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Analysis;

/// <param name="GroupsDone">Units of work stored so far (groups, and members of mixed groups analysed one by one).</param>
/// <param name="LastGroupKey">The key of the last stored unit; informational.</param>
/// <param name="FailedIds">Members whose model output stayed invalid; a resume skips them instead of retrying.</param>
/// <param name="IndividualIds">Remaining members of a mixed group; a resume analyses them one by one, never derived.</param>
/// <param name="CandidateIds">
/// The run's candidates, frozen at the start; a resume covers exactly these (minus stored, failed and no longer
/// eligible ones), so mail fetched meanwhile never shifts the window.
/// </param>
public sealed record AnalysisRunCursor(
    Guid RunId,
    int GroupsDone = 0,
    string? LastGroupKey = null,
    IReadOnlyList<string>? FailedIds = null,
    IReadOnlyList<string>? IndividualIds = null,
    IReadOnlyList<string>? CandidateIds = null);

/// <summary>
/// One analysis run (DESIGN §6.2, epic #22): groups the run's remaining frozen candidates, then per group asks the model
/// about the representatives, derives the other members when the representatives agree and stores everything with the
/// run counters and the checkpoint in one transaction. The model call is not idempotent, so a restart repeats at most
/// the group whose transaction did not commit; members with a suggestion of this run are not candidates any more.
/// </summary>
public sealed partial class AnalysisRunJob(
    AppDbContext db,
    ILlmClientFactory llm,
    IGmailClient gmail,
    ISettingsStore settingsStore,
    AnalysisGrouper grouper,
    IAnalysisShortCircuit shortCircuit,
    SenderStatsUpdater senderStats,
    IOptions<LlmOptions> llmOptions,
    TimeProvider time,
    ILogger<AnalysisRunJob> logger) : IJobHandler
{
    public const string JobType = AnalysisJobTypes.Run;
    public const string Queue = JobQueues.Analysis;
    public const int MaxErrorLength = 300;

    public string Type => JobType;

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<AnalysisRunCursor>()
            ?? throw new JobRefusedException("The analysis job has no run.");
        var run = await db.AnalysisRuns.SingleOrDefaultAsync(r => r.Id == cursor.RunId, ct)
            ?? throw new JobRefusedException("The analysis run no longer exists.");
        if (run.Status is AnalysisRunStatus.Completed or AnalysisRunStatus.Cancelled)
        {
            return;
        }

        try
        {
            await RunCoreAsync(ctx, run, cursor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Finished groups are committed; the run records why it stopped and the job fails with the same reason.
            db.ChangeTracker.Clear();
            await FinishRunAsync(run.Id, AnalysisRunStatus.Failed, Shorten(ex.Message), CancellationToken.None);
            throw;
        }
    }

    private async Task RunCoreAsync(JobContext ctx, AnalysisRunRow run, AnalysisRunCursor cursor, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var model = settings.ChatModel ?? throw new LlmNotConfiguredException(ModelKinds.Chat);
        var builder = new AnalysisPromptBuilder(PromptTemplate.FromSettings(settings.AnalysisPromptTemplate));

        run.Status = AnalysisRunStatus.Running;
        run.StartedAt ??= time.GetUtcNow();
        run.FinishedAt = null;
        run.Error = null;
        run.Model = model;
        run.PromptVersion = builder.Version;
        await db.SaveChangesAsync(ct);

        var work = await PlanAsync(run, cursor, settings, ct);
        cursor = work.Cursor;
        using var chat = await llm.CreateChatClientAsync(ct);
        var context = new RunContext(run, settings, builder, chat, await UserLabelsAsync(ct), work.Allowlisted);

        var front = new Queue<MessageGroup>(work.Individual);
        var rest = new Queue<MessageGroup>(work.Groups);
        var individualIds = new HashSet<string>(cursor.IndividualIds ?? [], StringComparer.Ordinal);
        var failedIds = new List<string>(cursor.FailedIds ?? []);
        while (front.TryDequeue(out var group) || rest.TryDequeue(out group))
        {
            var outcome = await AnalyseGroupAsync(context, group, ct);
            foreach (var single in outcome.Individual)
            {
                front.Enqueue(single);
                individualIds.Add(single.Members[0].Id);
            }

            individualIds.ExceptWith(group.Members.Select(m => m.Id).Except(outcome.Individual.Select(g => g.Members[0].Id)));
            failedIds.AddRange(outcome.FailedIds);
            cursor = cursor with
            {
                GroupsDone = cursor.GroupsDone + 1,
                LastGroupKey = group.Key,
                FailedIds = [.. failedIds],
                IndividualIds = [.. individualIds],
            };

            var signal = await StoreAsync(ctx, run, group, outcome, cursor, ct);
            if (signal == JobSignal.Cancel)
            {
                await FinishRunAsync(run.Id, AnalysisRunStatus.Cancelled, null, ct);
                return;
            }

            if (signal == JobSignal.Pause)
            {
                return;
            }
        }

        await ctx.CompleteAsync(cursor, Progress(run, cursor), async c =>
        {
            run.Status = AnalysisRunStatus.Completed;
            run.FinishedAt = time.GetUtcNow();
            await db.SaveChangesAsync(c);
        }, ct);
    }

    private sealed record Plan(
        IReadOnlyList<MessageGroup> Individual, IReadOnlyList<MessageGroup> Groups, IReadOnlySet<string> Allowlisted, AnalysisRunCursor Cursor);

    /// <summary>
    /// The frozen candidates still to cover, newest first: not failed, without a suggestion of this run and still
    /// eligible. Candidates no longer eligible leave the cursor and count as skipped (both stored with the next
    /// checkpoint). Members left over from a mixed group come first, one by one.
    /// </summary>
    private async Task<Plan> PlanAsync(AnalysisRunRow run, AnalysisRunCursor cursor, AppSettings settings, CancellationToken ct)
    {
        var frozen = cursor.CandidateIds ?? throw new JobRefusedException("The analysis run has no frozen candidates.");
        var failed = (cursor.FailedIds ?? []).ToHashSet(StringComparer.Ordinal);
        var stored = await db.Suggestions.AsNoTracking()
            .Where(s => s.RunId == run.Id)
            .Select(s => s.MessageId)
            .ToListAsync(ct);
        var open = frozen.Except(failed, StringComparer.Ordinal).Except(stored, StringComparer.Ordinal).ToArray();
        var rows = await db.Messages.AsNoTracking().Where(m => open.Contains(m.Id)).ToListAsync(ct);
        var candidates = rows
            .Where(m => AnalysisCandidates.IsEligible(run.Scope, m))
            .OrderByDescending(m => m.InternalDate)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        var dropped = open.Except(candidates.Select(m => m.Id), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (dropped.Count > 0)
        {
            run.SkippedMessages += dropped.Count;
            cursor = cursor with
            {
                CandidateIds = [.. frozen.Where(id => !dropped.Contains(id))],
                IndividualIds = cursor.IndividualIds?.Where(id => !dropped.Contains(id)).ToList(),
            };
        }

        var addresses = candidates.Select(m => m.FromAddress).Distinct().ToArray();
        var allowlisted = (await db.Senders.AsNoTracking()
                .Where(s => s.Allowlisted && addresses.Contains(s.Address))
                .Select(s => s.Address)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var individual = (cursor.IndividualIds ?? []).ToHashSet(StringComparer.Ordinal);
        var grouping = GroupingSettings.From(settings) with { Mode = run.GroupingMode };
        var groups = await grouper.GroupAsync([.. candidates.Where(m => !individual.Contains(m.Id))], grouping, allowlisted, ct);
        return new Plan([.. candidates.Where(m => individual.Contains(m.Id)).Select(AnalysisGrouper.Single)], groups, allowlisted, cursor);
    }

    /// <summary>The mailbox's user labels, sorted; read once per run for the prompt.</summary>
    private async Task<IReadOnlyList<string>> UserLabelsAsync(CancellationToken ct) =>
        (await gmail.ListLabelsAsync(ct))
            .Where(l => l.Type == GmailLabelType.User)
            .Select(l => l.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// One transaction: the group's suggestion rows (replacing a pending or rejected suggestion of a re-analysed
    /// message), the members' status, the senders' analysed counts, the run counters and the job checkpoint. A member
    /// approved or applied meanwhile keeps its suggestion and counts as skipped. Returns the pause/cancel signal.
    /// </summary>
    private async Task<JobSignal> StoreAsync(
        JobContext ctx, AnalysisRunRow run, MessageGroup group, GroupOutcome outcome, AnalysisRunCursor cursor, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var members = group.Members.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var ids = outcome.Suggestions.Select(s => s.MessageId).ToArray();
        var decided = (await db.Suggestions.AsNoTracking()
                .Where(s => ids.Contains(s.MessageId) && (s.Status == SuggestionStatus.Approved || s.Status == SuggestionStatus.Applied))
                .Select(s => s.MessageId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var rows = outcome.Suggestions.Where(s => !decided.Contains(s.MessageId)).ToList();

        run.MessagesCovered += rows.Count;
        run.MessagesLlm += rows.Count(s => s.Source == SuggestionSource.Llm);
        run.MessagesDerived += rows.Count(s => s.Source == SuggestionSource.Derived);
        run.MessagesFromMemory += rows.Count(s => s.Source == SuggestionSource.Memory);
        run.FailedMessages += outcome.FailedIds.Count;
        run.SkippedMessages += decided.Count;
        run.LlmCalls += outcome.LlmCalls;
        run.Groups++;
        run.MixedGroups += outcome.Mixed ? 1 : 0;

        return await ctx.CheckpointAsync(cursor, Progress(run, cursor), async c =>
        {
            var replaced = rows.Select(r => r.MessageId).ToArray();
            await db.Suggestions
                .Where(s => replaced.Contains(s.MessageId) && (s.Status == SuggestionStatus.Pending || s.Status == SuggestionStatus.Rejected))
                .ExecuteDeleteAsync(c);
            foreach (var row in rows)
            {
                var message = members[row.MessageId];
                db.Messages.Attach(message);
                row.RunId = run.Id;
                row.CreatedAt = now;
                db.Suggestions.Add(row);
                row.SetStatus(SuggestionStatus.Pending, message, now);
            }

            await db.SaveChangesAsync(c);
            await senderStats.UpdateAnalysedCountsAsync(rows.Select(s => s.SenderAddress), c);
        }, ct);
    }

    /// <summary>Sets a final status unless the run already has one; not tracked, so it works after a failed save.</summary>
    private async Task FinishRunAsync(Guid runId, AnalysisRunStatus status, string? error, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await db.AnalysisRuns
            .Where(r => r.Id == runId && (r.Status == AnalysisRunStatus.Queued || r.Status == AnalysisRunStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.Error, error)
                .SetProperty(r => r.FinishedAt, now), ct);
    }

    /// <summary>Covered and failed candidates out of the frozen ones (skipped ones have left the cursor).</summary>
    private static JobProgress Progress(AnalysisRunRow run, AnalysisRunCursor cursor) =>
        new(run.MessagesCovered + run.FailedMessages, cursor.CandidateIds?.Count, $"{run.Groups} groups, {run.LlmCalls} LLM calls");

    private static string Shorten(string message) =>
        message.Length <= MaxErrorLength ? message : message[..(MaxErrorLength - 1)] + "…";

    private sealed record RunContext(
        AnalysisRunRow Run,
        AppSettings Settings,
        AnalysisPromptBuilder Builder,
        IChatClient Chat,
        IReadOnlyList<string> LabelTree,
        IReadOnlySet<string> Allowlisted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Analysis output for {Count} email(s) stayed invalid after a retry: {Errors}")]
    private static partial void LogInvalidOutput(ILogger logger, int count, string errors);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped {Count} representative(s) Gmail no longer knows")]
    private static partial void LogMissingBodies(ILogger logger, int count);
}

/// <summary>The chat model could not be reached or did not answer in time; fails the run.</summary>
public sealed class AnalysisModelUnavailableException(string message, Exception inner) : Exception(message, inner);
