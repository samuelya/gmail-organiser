using System.Text.Json;
using System.Text.RegularExpressions;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Review;

/// <param name="ApprovedBefore">Only suggestions approved at or before this (the batch's creation) are applied.</param>
/// <param name="Total">The appliable suggestions counted when the batch was created.</param>
/// <param name="SuggestionIds">Null for every approved suggestion (of <paramref name="SenderAddress"/>).</param>
/// <param name="Pending">The chunk whose undo log is written and whose <c>batchModify</c> may have been sent.</param>
/// <param name="Skipped">Messages Gmail refused one by one; their suggestions stay approved and are not retried.</param>
public sealed record ApplyCursor(
    Guid BatchId,
    DateTimeOffset ApprovedBefore,
    int Total,
    string? SenderAddress = null,
    Guid[]? SuggestionIds = null,
    int ChunksDone = 0,
    int MessagesDone = 0,
    ApplyChunk? Pending = null,
    ApplySkip[]? Skipped = null);

/// <summary>One <c>batchModify</c> call: the same label ids added and removed on every message.</summary>
public sealed record ApplyChunk(string[] MessageIds, string[] Add, string[] Remove);

public sealed record ApplySkip(Guid SuggestionId, string MessageId, string Reason);

/// <summary>
/// Applies approved suggestions to Gmail (DESIGN §6.3). Messages with the same label changes are sent together in
/// chunks of at most <see cref="GmailOptions.BatchModifyMaxIds"/>. Each chunk is two transactions around the Gmail
/// call: the first locks the suggestions and messages, re-plans them, writes the undo log, marks them applied and
/// checkpoints the chunk as pending; the second updates the stored labels and counts and clears it. A pending chunk
/// is finished by a send that succeeds, or reconciled with Gmail after <see cref="MaxSendFailures"/> refused
/// re-sends; it is reverted only when this run sent it for the first time and Gmail certainly changed nothing.
/// </summary>
public sealed partial class ApplyActionsJob(
    AppDbContext db,
    IGmailClient gmail,
    LabelCatalog catalog,
    LabelResolver labels,
    RepliedThreadChecker repliedThreads,
    ISettingsStore settingsStore,
    IOptions<GmailOptions> gmailOptions,
    TimeProvider time,
    ILogger<ApplyActionsJob> logger) : IJobHandler, IJobCancelHook
{
    public const string JobType = ReviewJobTypes.Apply;
    public const string Queue = JobQueues.Apply;
    public const string InvalidLabelReason = "invalid label";

    // Suggestions of the current plan whose topic label Gmail would refuse; reported in every progress update.
    private int invalidLabels;

    public string Type => JobType;

    /// <summary>The suggestions a batch applies: approved by <paramref name="approvedBefore"/>, message not deleted in Gmail.</summary>
    public static IQueryable<SuggestionRow> Eligible(
        AppDbContext db, DateTimeOffset approvedBefore, string? senderAddress, Guid[]? suggestionIds)
    {
        var query = db.Suggestions.Where(s => s.Status == SuggestionStatus.Approved
            && s.DecidedAt <= approvedBefore
            && db.Messages.Any(m => m.Id == s.MessageId && !m.DeletedInGmail));
        if (senderAddress is not null)
        {
            query = query.Where(s => s.SenderAddress == senderAddress);
        }

        if (suggestionIds is not null)
        {
            query = query.Where(s => suggestionIds.Contains(s.Id));
        }

        return query;
    }

    public static string Describe(int count, string? senderAddress) =>
        $"Apply {count} suggestion{(count == 1 ? "" : "s")}{(senderAddress is null ? "" : $" for {senderAddress}")}";

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<ApplyCursor>() ?? throw new JobRefusedException("The apply job has no batch.");
        if (!await db.ActionBatches.AnyAsync(b => b.Id == cursor.BatchId, ct))
        {
            throw new JobRefusedException("The action batch no longer exists.");
        }

        try
        {
            await RunCoreAsync(ctx, cursor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            await MarkPartialAsync(ctx.ReadCursor<ApplyCursor>() ?? cursor);
            throw;
        }
    }

    /// <summary>A chunk that may have reached Gmail must be finished by a resume, not cancelled.</summary>
    public bool AllowsCancel(string? cursor) => Read(cursor)?.Pending is null;

    public async Task CancelledAsync(string? cursor, CancellationToken ct)
    {
        if (Read(cursor) is { } applied)
        {
            await MarkPartialAsync(applied);
        }
    }

    private async Task RunCoreAsync(JobContext ctx, ApplyCursor cursor, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var plan = await PlanAsync(cursor, settings, ct);
        var total = cursor.MessagesDone + (cursor.Pending?.MessageIds.Length ?? 0) + plan.Messages;
        if (cursor.Pending is not null)
        {
            (cursor, var signal, total) = await SendAsync(ctx, cursor, resent: true, total, ct);
            if (await StoppedAsync(signal, cursor))
            {
                return;
            }
        }

        while (true)
        {
            var replan = false;
            foreach (var chunk in plan.Chunks)
            {
                if (await PrepareAsync(ctx, cursor, chunk, plan, settings, total, ct) is not { } prepared)
                {
                    replan = true;
                    break;
                }

                (cursor, var signal, total) = await SendAsync(ctx, prepared, resent: false, total, ct);
                if (await StoppedAsync(signal, cursor))
                {
                    return;
                }
            }

            if (!replan)
            {
                var progress = Progress(cursor, cursor.MessagesDone);
                await ctx.CompleteAsync(cursor, progress, t => SetDescriptionAsync(cursor.BatchId, null, t), ct);
                return;
            }

            // Fresh settings: the locked re-check reads them as stored now, so the new plan must agree with it.
            settings = await settingsStore.GetAsync(ct);
            plan = await PlanAsync(cursor, settings, ct);
            total = cursor.MessagesDone + plan.Messages;
        }
    }

    /// <summary>
    /// Plans every eligible suggestion; topic labels Gmail would refuse are skipped and stay approved. Runs before any
    /// chunk is prepared, so a Gmail error in the replied-thread check leaves the cursor as it was.
    /// </summary>
    private async Task<Plan> PlanAsync(ApplyCursor cursor, AppSettings settings, CancellationToken ct)
    {
        var skipped = (cursor.Skipped ?? []).Select(s => s.SuggestionId).ToArray();
        var rows = await Eligible(db, cursor.ApprovedBefore, cursor.SenderAddress, cursor.SuggestionIds)
            .Where(s => !skipped.Contains(s.Id))
            .AsNoTracking()
            .Join(db.Messages.AsNoTracking(), s => s.MessageId, m => m.Id, (s, m) => new { Suggestion = s, Message = m })
            .OrderBy(x => x.Suggestion.Id)
            .ToListAsync(ct);
        var allowlisted = await AllowlistLoader.LoadAsync(db, settings, ct);
        var valid = rows.Where(r => LabelResolver.IsValid(r.Suggestion.TopicLabel)).ToList();
        invalidLabels = rows.Count - valid.Count;
        if (settings.Protection.RepliedThreads)
        {
            // Mark time (#177): only messages that would otherwise get the delete label cost a thread lookup.
            await repliedThreads.CheckAsync(
                [.. valid
                    .Where(r => r.Suggestion.ToBeDeleted && !MessageProtection.IsProtected(r.Message, allowlisted, settings.Protection))
                    .Select(r => r.Message)],
                ct);
        }

        var paths = valid.Select(r => r.Suggestion.TopicLabel).ToList();
        if (valid.Any(r => r.Suggestion.NeedsAction))
        {
            paths.Add(SettingLabel(settings.ActionLabelName, nameof(AppSettings.ActionLabelName)));
        }

        if (valid.Any(r => r.Suggestion.ToBeDeleted && !MessageProtection.IsProtected(r.Message, allowlisted, settings.Protection)))
        {
            paths.Add(SettingLabel(settings.DeleteLabelName, nameof(AppSettings.DeleteLabelName)));
        }

        var labelIds = valid.Count == 0
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : await labels.EnsureAsync(paths, (label, _) => RecordCreatedAsync(cursor.BatchId, label), ct);
        // Ids, not names: a replaced label renamed in Gmail since analysis is still removed; a deleted one is skipped.
        var personal = valid.Any(r => r.Suggestion.ReplaceLabelIds.Length > 0)
            ? PersonalLabels.From(await catalog.GetAsync(ct), settings).Names
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var removable = personal.Keys.ToHashSet(StringComparer.Ordinal);
        var items = valid.Select(r => new PlannedItem(
            r.Suggestion,
            ActionPlanner.Plan(r.Suggestion, r.Message, labelIds, settings, allowlisted, removable)));
        var chunks = LabelChunks.Group(items, i => i.Plan.Add, i => i.Plan.Remove, gmailOptions.Value.BatchModifyMaxIds)
            .Select(c => new PlannedChunk(c.Items, c.Add, c.Remove))
            .ToList();
        var names = labelIds.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
        foreach (var (id, name) in personal)
        {
            names.TryAdd(id, name);
        }

        return new Plan(chunks, labelIds, removable, names);
    }

    /// <summary>
    /// Locks the chunk's suggestions and messages, re-plans each message from its locked row (protection and labels
    /// as stored now), writes the undo log, marks the suggestions applied and checkpoints the chunk as pending, in one
    /// transaction. Null when anything changed since planning: nothing was written, the caller replans.
    /// </summary>
    private async Task<ApplyCursor?> PrepareAsync(
        JobContext ctx, ApplyCursor cursor, PlannedChunk chunk, Plan plan, AppSettings settings, int total, CancellationToken ct)
    {
        var ids = chunk.Items.Select(i => i.Suggestion.Id).ToArray();
        var messageIds = chunk.Items.Select(i => i.Suggestion.MessageId).ToArray();
        var next = cursor with { Pending = new ApplyChunk(messageIds, chunk.Add, chunk.Remove) };
        try
        {
            await ctx.CheckpointAsync(next, Progress(cursor, total), async t =>
            {
                // Suggestions before messages, each by id: the review endpoints lock in the same order.
                var locked = await db.Suggestions
                    .FromSql($"SELECT * FROM suggestions WHERE id = ANY({ids}) ORDER BY id FOR UPDATE")
                    .ToListAsync(t);
                var messages = (await db.Messages
                    .FromSql($"SELECT * FROM messages WHERE id = ANY({messageIds}) ORDER BY id FOR UPDATE")
                    .ToListAsync(t)).ToDictionary(m => m.Id, StringComparer.Ordinal);
                if (locked.Count != ids.Length
                    || locked.Any(s => s.Status != SuggestionStatus.Approved || s.DecidedAt > cursor.ApprovedBefore)
                    || messages.Count != messageIds.Length
                    || messages.Values.Any(m => m.DeletedInGmail))
                {
                    throw new PlanChangedException();
                }

                // Both halves as stored now: an address or a domain allowlisted since the job started is honoured.
                var allowlisted = await AllowlistLoader.LoadAsync(
                    db, await settingsStore.GetAsync(t), [.. messages.Values.Select(m => m.FromAddress).Distinct()], t);
                var now = time.GetUtcNow();
                foreach (var suggestion in locked)
                {
                    var message = messages[suggestion.MessageId];
                    var fresh = Replan(suggestion, message, plan, settings, allowlisted);
                    if (!Sorted(fresh.Add).SequenceEqual(chunk.Add) || !Sorted(fresh.Remove).SequenceEqual(chunk.Remove))
                    {
                        throw new PlanChangedException();
                    }

                    db.ActionLog.Add(new ActionLogRow
                    {
                        Id = Guid.CreateVersion7(now),
                        BatchId = cursor.BatchId,
                        MessageId = message.Id,
                        SuggestionId = suggestion.Id,
                        LabelsAdded = [.. chunk.Add.Select(id => plan.Names.GetValueOrDefault(id, id))],
                        LabelsRemoved = [.. chunk.Remove.Select(id => plan.Names.GetValueOrDefault(id, id))],
                        LabelIdsBefore = message.LabelIds,
                        LabelIdsAfter = After(message.LabelIds, chunk.Add, chunk.Remove),
                        Note = fresh.Note,
                        CreatedAt = now,
                    });
                    suggestion.SetStatus(SuggestionStatus.Applied, message, now);
                }

                await db.SaveChangesAsync(t);
            }, ct);
        }
        catch (PlanChangedException)
        {
            db.ChangeTracker.Clear();
            return null;
        }

        db.ChangeTracker.Clear();
        return next;
    }

    /// <summary>The plan for a locked row; a label the earlier plan did not resolve (it was not needed then) means it changed.</summary>
    private static ActionPlan Replan(SuggestionRow suggestion, MessageRow message, Plan plan, AppSettings settings, Allowlist allowlisted)
    {
        try
        {
            return ActionPlanner.Plan(suggestion, message, plan.LabelIds, settings, allowlisted, plan.Removable);
        }
        catch (KeyNotFoundException)
        {
            throw new PlanChangedException();
        }
    }

    /// <summary>Adds a label this batch created to the batch, so undo (#113) can tell it from the user's own.</summary>
    private Task RecordCreatedAsync(Guid batchId, GmailLabel label) =>
        db.Database.ExecuteSqlAsync(
            $"UPDATE action_batches SET created_label_ids = array_append(created_label_ids, {label.Id}) WHERE id = {batchId}",
            CancellationToken.None);

    /// <summary>A cancelled run marks the batch partial; a pause leaves it for the resume.</summary>
    private async Task<bool> StoppedAsync(JobSignal signal, ApplyCursor cursor)
    {
        if (signal == JobSignal.Cancel)
        {
            await MarkPartialAsync(cursor);
        }

        return signal != JobSignal.Continue;
    }

    private async Task MarkPartialAsync(ApplyCursor cursor)
    {
        try
        {
            await SetDescriptionAsync(cursor.BatchId, $" (partial: {cursor.MessagesDone} of {cursor.Total})", CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogPartialNotRecorded(logger, ex);
        }
    }

    /// <summary>The batch's description without any earlier partial suffix, plus <paramref name="suffix"/>.</summary>
    private async Task SetDescriptionAsync(Guid batchId, string? suffix, CancellationToken ct)
    {
        var batch = await db.ActionBatches.SingleAsync(b => b.Id == batchId, ct);
        batch.Description = PartialSuffix().Replace(batch.Description, "") + suffix;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Messages applied of the total, and every skip with its reason (invalid labels first).</summary>
    private JobProgress Progress(ApplyCursor cursor, int total)
    {
        var reasons = Enumerable.Repeat(InvalidLabelReason, invalidLabels)
            .Concat((cursor.Skipped ?? []).Select(s => s.Reason))
            .GroupBy(r => r)
            .Select(g => (Reason: g.Key, Count: g.Count()))
            .ToList();
        var skipped = reasons.Sum(r => r.Count);
        var detail = reasons.Count == 1 ? reasons[0].Reason : string.Join(", ", reasons.Select(r => $"{r.Count} {r.Reason}"));
        return new(cursor.MessagesDone, total,
            $"Applied {cursor.MessagesDone} of {total} messages{(skipped > 0 ? $"; {skipped} skipped ({detail})" : "")}");
    }

    private static ApplyCursor? Read(string? cursor) =>
        cursor is null ? null : JsonSerializer.Deserialize<ApplyCursor>(cursor, JobRow.Json);

    private static string[] After(string[] before, string[] add, string[] remove) => LabelChunks.After(before, add, remove);

    private static string[] Sorted(IEnumerable<string> ids) => LabelChunks.Sorted(ids);

    private static string SettingLabel(string path, string setting) =>
        LabelResolver.IsValid(path) ? path : throw new JobRefusedException($"The {setting} setting is not a valid Gmail label.");

    [GeneratedRegex(@" \(partial: \d+ of \d+\)$")]
    private static partial Regex PartialSuffix();

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not mark the action batch as partial.")]
    private static partial void LogPartialNotRecorded(ILogger logger, Exception exception);

    private sealed record PlannedItem(SuggestionRow Suggestion, ActionPlan Plan);

    private sealed record PlannedChunk(IReadOnlyList<PlannedItem> Items, string[] Add, string[] Remove);

    /// <param name="LabelIds">Label path to id (case-insensitive), as resolved for this plan.</param>
    /// <param name="Removable">Ids of the personal labels Gmail had when planned; the replaced labels apply may remove.</param>
    /// <param name="Names">Label id to the path it was resolved from, for the log's display names.</param>
    private sealed record Plan(
        IReadOnlyList<PlannedChunk> Chunks,
        IReadOnlyDictionary<string, string> LabelIds,
        IReadOnlySet<string> Removable,
        IReadOnlyDictionary<string, string> Names)
    {
        public int Messages => Chunks.Sum(c => c.Items.Count);
    }

    private sealed class PlanChangedException : Exception;
}
