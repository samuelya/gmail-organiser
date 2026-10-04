using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Llm;
using GmailOrganiser.Memory;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GmailOrganiser.Analysis;

/// <param name="GroupsDone">Units of work stored so far (groups, and members of mixed groups analysed one by one).</param>
/// <param name="LastGroupKey">The key of the last stored unit; informational.</param>
/// <param name="FailedIds">Members whose model output stayed invalid; a resume skips them instead of retrying.</param>
/// <param name="IndividualIds">Remaining members of a mixed group; a resume analyses them one by one, never derived.</param>
/// <param name="CandidateIds">
/// The run's candidates, frozen at the start; a resume covers exactly these (minus stored, failed and no longer
/// eligible ones), so mail fetched meanwhile never shifts the window.
/// </param>
/// <param name="SuggestionIds">A compare run's suggestion per candidate, frozen at the start; its alternative belongs to it.</param>
/// <param name="CoveredIds">
/// A compare run's stored candidates (alternative written, or suggestion gone and skipped); a resume skips them even
/// when another run replaced or a cascade removed their alternatives meanwhile.
/// </param>
public sealed record AnalysisRunCursor(
    Guid RunId,
    int GroupsDone = 0,
    string? LastGroupKey = null,
    IReadOnlyList<string>? FailedIds = null,
    IReadOnlyList<string>? IndividualIds = null,
    IReadOnlyList<string>? CandidateIds = null,
    IReadOnlyDictionary<string, Guid>? SuggestionIds = null,
    IReadOnlyList<string>? CoveredIds = null);

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
    IDecisionMemory memory,
    SenderStatsUpdater senderStats,
    IAttachmentPolicy attachmentPolicy,
    AttachmentPromptSection attachments,
    IOptions<LlmOptions> llmOptions,
    TimeProvider time,
    ILogger<AnalysisRunJob> logger) : IJobHandler
{
    public const string JobType = AnalysisJobTypes.Run;
    public const string Queue = JobQueues.Analysis;
    public const int MaxErrorLength = 300;
    private const int MaxStoreAttempts = 3;

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

        if (run.StartedAt is null)
        {
            run.DocumentTypeParent = settings.DocumentTypeParent;
        }

        run.Status = AnalysisRunStatus.Running;
        run.StartedAt ??= time.GetUtcNow();
        run.FinishedAt = null;
        run.Error = null;
        run.Model = model;
        run.PromptVersion = builder.Version;
        await db.SaveChangesAsync(ct);

        var (labelTree, labels) = await UserLabelsAsync(settings, ct);
        var work = await PlanAsync(run, cursor, settings, labels, ct);
        cursor = work.Cursor;
        using var chat = await llm.CreateChatClientAsync(ct);
        var compare = run.Kind == AnalysisRunKind.Compare;
        // A compare run's hints leave out decisions about every message it re-analyses, not just the current group's.
        var context = new RunContext(
            run, settings, builder, chat, labelTree, new LabelTreeIndex(labelTree), labels, work.Allowlisted, await attachmentPolicy.GetAsync(ct),
            compare ? [.. cursor.SuggestionIds!.Keys] : []);

        var front = new Queue<MessageGroup>(work.Individual);
        var rest = new Queue<MessageGroup>(work.Groups);
        var individualIds = new HashSet<string>(cursor.IndividualIds ?? [], StringComparer.Ordinal);
        var failedIds = new List<string>(cursor.FailedIds ?? []);
        var coveredIds = new List<string>(cursor.CoveredIds ?? []);
        var prepared = new Dictionary<MessageGroup, PreparedGroup>(ReferenceEqualityComparer.Instance);
        while (front.TryDequeue(out var group) || rest.TryDequeue(out group))
        {
            if (!prepared.Remove(group, out var ready))
            {
                await PrepareAsync(context, [group, .. front.Take(MemoryLookaheadGroups), .. rest.Take(MemoryLookaheadGroups)], prepared, ct);
                prepared.Remove(group, out ready);
            }

            var outcome = await AnalyseGroupAsync(context, group, ready!, ct);
            foreach (var single in outcome.Individual)
            {
                front.Enqueue(single);
                individualIds.Add(single.Members[0].Id);
            }

            individualIds.ExceptWith(group.Members.Select(m => m.Id).Except(outcome.Individual.Select(g => g.Members[0].Id)));
            failedIds.AddRange(outcome.FailedIds);
            if (compare)
            {
                coveredIds.AddRange(outcome.Suggestions.Select(s => s.MessageId));
            }

            cursor = cursor with
            {
                GroupsDone = cursor.GroupsDone + 1,
                LastGroupKey = group.Key,
                FailedIds = [.. failedIds],
                IndividualIds = [.. individualIds],
                CoveredIds = compare ? [.. coveredIds] : null,
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
        IReadOnlyList<MessageGroup> Individual, IReadOnlyList<MessageGroup> Groups, Allowlist Allowlisted, AnalysisRunCursor Cursor);

    /// <summary>
    /// The frozen candidates still to cover, newest first: not failed, without a suggestion of this run (a compare
    /// run: not covered per its cursor) and still eligible. Candidates no longer eligible leave the cursor and count
    /// as skipped (both stored with the next checkpoint). Members left over from a mixed group come first, one by one.
    /// </summary>
    private async Task<Plan> PlanAsync(
        AnalysisRunRow run, AnalysisRunCursor cursor, AppSettings settings, PersonalLabels labels, CancellationToken ct)
    {
        var frozen = cursor.CandidateIds ?? throw new JobRefusedException("The analysis run has no frozen candidates.");
        var failed = (cursor.FailedIds ?? []).ToHashSet(StringComparer.Ordinal);
        var compare = run.Kind == AnalysisRunKind.Compare;
        var stored = compare
            ? cursor.CoveredIds ?? []
            : await db.Suggestions.AsNoTracking().Where(s => s.RunId == run.Id).Select(s => s.MessageId).ToListAsync(ct);
        var open = frozen.Except(failed, StringComparer.Ordinal).Except(stored, StringComparer.Ordinal).ToArray();
        var rows = await db.Messages.AsNoTracking().Where(m => open.Contains(m.Id)).ToListAsync(ct);
        var current = compare ? await CurrentSuggestionsAsync(cursor, ct) : null;
        var candidates = rows
            .Where(m => current is null
                ? AnalysisCandidates.IsEligible(run.Scope, m, labels)
                : AnalysisCandidates.IsCompareEligible(m, current))
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

        var allowlisted = await AllowlistLoader.LoadAsync(db, settings, ct);

        var individual = (cursor.IndividualIds ?? []).ToHashSet(StringComparer.Ordinal);
        var grouping = GroupingSettings.From(settings) with { Mode = run.GroupingMode };
        var groups = await grouper.GroupAsync([.. candidates.Where(m => !individual.Contains(m.Id))], grouping, allowlisted, labels, ct);
        return new Plan([.. candidates.Where(m => individual.Contains(m.Id)).Select(AnalysisGrouper.Single)], groups, allowlisted, cursor);
    }

    /// <summary>The mailbox's user label names, sorted, and the person's own labels; read once per run.</summary>
    private async Task<(IReadOnlyList<string> Tree, PersonalLabels Labels)> UserLabelsAsync(AppSettings settings, CancellationToken ct)
    {
        var labels = await gmail.ListLabelsAsync(ct);
        return (
            [.. labels.Where(l => l.Type == GmailLabelType.User).Select(l => l.Name).Order(StringComparer.OrdinalIgnoreCase)],
            PersonalLabels.From(labels, settings));
    }

    /// <summary>
    /// One transaction: the group's suggestion rows (replacing a pending or rejected suggestion of a re-analysed
    /// message), the members' status, the senders' analysed counts, the run counters and the job checkpoint. A member
    /// approved or applied meanwhile (also while this transaction waits for its row locks) keeps its suggestion and
    /// counts as skipped. Returns the pause/cancel signal.
    /// </summary>
    private async Task<JobSignal> StoreAsync(
        JobContext ctx, AnalysisRunRow run, MessageGroup group, GroupOutcome outcome, AnalysisRunCursor cursor, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var members = group.Members.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var ids = outcome.Suggestions.Select(s => s.MessageId).ToArray();
        // A compare run writes alternatives next to suggestions of any status.
        HashSet<string> decided = run.Kind == AnalysisRunKind.Compare ? [] : (await db.Suggestions.AsNoTracking()
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
        run.AttachmentsConverted += outcome.AttachmentsConverted;
        run.AttachmentsSkipped += outcome.AttachmentsSkipped;

        // A unique violation means another writer committed a suggestion for a member after the re-check: the retry's
        // re-check then skips it as decided meanwhile (or replaces it if undecided); it never fails the run.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ctx.CheckpointAsync(
                    cursor,
                    Progress(run, cursor),
                    c => run.Kind == AnalysisRunKind.Compare
                        ? WriteAlternativesAsync(run, cursor.SuggestionIds!, rows, now, c)
                        : WriteGroupAsync(run, members, rows, now, c),
                    ct);
            }
            catch (DbUpdateException ex) when (attempt < MaxStoreAttempts && IsSuggestionConflict(ex))
            {
                foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is SuggestionRow or MessageRow).ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    /// <summary>The group's rows under the members' locks; adjusts the run counters for members decided meanwhile.</summary>
    private async Task WriteGroupAsync(
        AnalysisRunRow run, Dictionary<string, MessageRow> members, List<SuggestionRow> rows, DateTimeOffset now, CancellationToken c)
    {
        // Suggestions, then messages, each by id: the review endpoints lock in the same order, so a group approve and
        // this checkpoint cannot deadlock. Apply-rest locks the sender's messages before inserting its suggestions, so
        // once the messages are locked, the read below sees every suggestion committed for a member meanwhile.
        var candidates = rows.Select(r => r.MessageId).ToArray();
        await db.Suggestions
            .FromSql($"SELECT * FROM suggestions WHERE message_id = ANY({candidates}) ORDER BY id FOR UPDATE")
            .AsNoTracking()
            .ToListAsync(c);
        await db.Database
            .SqlQuery<string>($"SELECT id AS \"Value\" FROM messages WHERE id = ANY({candidates}) ORDER BY id FOR UPDATE")
            .ToListAsync(c);

        // A review or apply-rest may have decided a member since the check in StoreAsync: skip it.
        var lateDecided = (await db.Suggestions.AsNoTracking()
                .Where(s => candidates.Contains(s.MessageId) && (s.Status == SuggestionStatus.Approved || s.Status == SuggestionStatus.Applied))
                .Select(s => s.MessageId)
                .ToListAsync(c))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var late in rows.Where(r => lateDecided.Contains(r.MessageId)))
        {
            run.MessagesCovered--;
            run.MessagesLlm -= late.Source == SuggestionSource.Llm ? 1 : 0;
            run.MessagesDerived -= late.Source == SuggestionSource.Derived ? 1 : 0;
            run.MessagesFromMemory -= late.Source == SuggestionSource.Memory ? 1 : 0;
            run.SkippedMessages++;
        }

        rows.RemoveAll(r => lateDecided.Contains(r.MessageId));
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
            db.Entry(message).Property(m => m.AnalysisStatus).IsModified = true;
            db.Entry(message).Property(m => m.UpdatedAt).IsModified = true;
        }

        await db.SaveChangesAsync(c);
        await senderStats.UpdateAnalysedCountsAsync(rows.Select(s => s.SenderAddress), c);
    }

    private static bool IsSuggestionConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, TableName: "suggestions" };

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
        new(run.MessagesCovered + run.FailedMessages, cursor.CandidateIds?.Count, $"{run.Groups} groups, {run.LlmCalls} LLM calls"
            + (run.AttachmentsConverted + run.AttachmentsSkipped == 0
                ? ""
                : $", {run.AttachmentsConverted} attachments converted, {run.AttachmentsSkipped} skipped"));

    private static string Shorten(string message) =>
        message.Length <= MaxErrorLength ? message : message[..(MaxErrorLength - 1)] + "…";

    private sealed record RunContext(
        AnalysisRunRow Run,
        AppSettings Settings,
        AnalysisPromptBuilder Builder,
        IChatClient Chat,
        IReadOnlyList<string> LabelTree,
        LabelTreeIndex LabelIndex,
        PersonalLabels Labels,
        Allowlist Allowlisted,
        AttachmentPolicySnapshot Attachments,
        IReadOnlyCollection<string> HintExclusions);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Analysis output for {Count} email(s) stayed invalid after a retry: {Errors}")]
    private static partial void LogInvalidOutput(ILogger logger, int count, string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Analysis output ignored: {Notes}")]
    private static partial void LogDroppedOutput(ILogger logger, string notes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped {Count} representative(s) Gmail no longer knows")]
    private static partial void LogMissingBodies(ILogger logger, int count);
}

/// <summary>The chat model could not be reached or did not answer in time; fails the run.</summary>
public sealed class AnalysisModelUnavailableException(string message, Exception inner) : Exception(message, inner);
