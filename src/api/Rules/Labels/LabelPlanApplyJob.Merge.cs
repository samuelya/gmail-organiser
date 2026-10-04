using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using Google;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Rules.Labels;

public sealed partial class LabelPlanApplyJob
{
    public static string DescribeMerge(string source, string target) => $"Merged label {source} into {target}";

    /// <summary>
    /// Moves every message of the source label to the target: a pending chunk is re-sent first, then the label's message
    /// ids are listed in full, Spam and Trash included (nothing changes while paging), and moved in chunks, each logged in
    /// the item's <see cref="ActionKind.LabelMerge"/> batch before its <c>batchModify</c>, as <see cref="UndoActionsJob"/>
    /// does. Then each affected filter is retargeted. Both steps re-run safely: moved mail no longer lists, a retargeted
    /// filter is found by its <c>restored_from</c>. Log rows and History use the label names at apply time, after any
    /// rename earlier in the plan.
    /// </summary>
    private async Task<(LabelPlanApplyCursor, JobSignal, LabelPlanItemStatus, string?)> MergeAsync(
        JobContext ctx, LabelPlanApplyCursor cursor, LabelPlanItem item, CancellationToken ct)
    {
        string? error;
        JobSignal signal;
        if (cursor.Pending is not null)
        {
            (cursor, signal, error) = await SendAsync(ctx, cursor, item, resent: true, ct);
            if (signal != JobSignal.Continue || error is not null)
            {
                return (cursor, signal, LabelPlanItemStatus.Failed, error);
            }
        }

        var labels = await catalog.RefreshAsync(ct);
        if (item.TargetLabelId is not { } target
            || labels.FirstOrDefault(l => l.Id == item.LabelId) is not { } source
            || labels.FirstOrDefault(l => l.Id == target) is not { } targetLabel)
        {
            return (cursor, JobSignal.Continue, LabelPlanItemStatus.Failed, MissingError);
        }

        var names = new MergeNames(source.Name, targetLabel.Name);
        var ids = await ListAsync([item.LabelId], ct);
        var hadTarget = (await ListAsync([item.LabelId, target], ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(gmailOptions.Value.BatchModifyMaxIds))
        {
            (cursor, signal) = await PrepareAsync(ctx, cursor, item, names, chunk, hadTarget, ct);
            if (signal != JobSignal.Continue)
            {
                // A pause or cancel came in while the chunk was prepared: Gmail has not seen it, so it is taken back.
                return (await RevertAsync(ctx, cursor, item), signal, LabelPlanItemStatus.Failed, null);
            }

            (cursor, signal, error) = await SendAsync(ctx, cursor, item, resent: false, ct);
            if (signal != JobSignal.Continue || error is not null)
            {
                return (cursor, signal, LabelPlanItemStatus.Failed, error);
            }
        }

        var errors = new List<string>();
        foreach (var filterId in item.AffectedFilterIds)
        {
            try
            {
                var summary = await db.Filters.AsNoTracking().Where(f => f.Id == filterId).Select(f => f.CriteriaSummary).SingleOrDefaultAsync(ct);
                var result = await filters.RetargetAsync(filterId, item.LabelId, target, ct);
                if (result.Outcome == FilterOutcome.Conflict)
                {
                    errors.Add($"filter {filterId}: {result.Detail}");
                }
                else if (result.Outcome == FilterOutcome.Ok)
                {
                    await RecordAsync(ctx.JobId, $"Filter {summary ?? filterId} moved from label {names.Source} to {names.Target}", ct);
                }
            }
            catch (Exception ex) when (ItemError(ex) is { } refused)
            {
                errors.Add($"filter {filterId}: {refused}");
            }
        }

        return errors.Count == 0
            ? (cursor, JobSignal.Continue, LabelPlanItemStatus.Applied, null)
            : (cursor, JobSignal.Continue, LabelPlanItemStatus.Failed, string.Join("; ", errors));
    }

    /// <summary>Every message id carrying all of <paramref name="labelIds"/>, Spam and Trash included, read page by page before anything changes.</summary>
    private async Task<List<string>> ListAsync(string[] labelIds, CancellationToken ct)
    {
        var ids = new List<string>();
        string? token = null;
        do
        {
            var query = new MessageListQuery(null, labelIds, token, MessageListQuery.MaxPageSize) { IncludeSpamTrash = true };
            var page = await gmail.ListMessageIdsAsync(query, ct);
            ids.AddRange(page.Messages.Select(m => m.Id));
            token = page.NextPageToken;
        }
        while (token is not null);

        return [.. ids.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Writes the chunk's log rows (and the item's batch with the first chunk) and checkpoints it as pending, in one
    /// transaction. Undo only needs what the merge changed, so the labels before are the source, plus the target for mail
    /// that already had it (<paramref name="hadTarget"/>): undo then re-adds the source and keeps such mail's target.
    /// </summary>
    private async Task<(LabelPlanApplyCursor, JobSignal)> PrepareAsync(
        JobContext ctx, LabelPlanApplyCursor cursor, LabelPlanItem item, MergeNames names, string[] chunk,
        HashSet<string> hadTarget, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var next = cursor with { BatchId = cursor.BatchId ?? Guid.CreateVersion7(now), Pending = chunk };
        var signal = await ctx.CheckpointAsync(next, Progress(cursor, names.Source), async t =>
        {
            if (cursor.BatchId is null)
            {
                db.ActionBatches.Add(new ActionBatchRow
                {
                    Id = next.BatchId!.Value,
                    Kind = ActionKind.LabelMerge,
                    Description = DescribeMerge(names.Source, names.Target),
                    JobId = ctx.JobId,
                    CreatedAt = now,
                });
            }

            foreach (var id in chunk)
            {
                string[] before = hadTarget.Contains(id) ? [item.LabelId, item.TargetLabelId!] : [item.LabelId];
                db.ActionLog.Add(new ActionLogRow
                {
                    Id = Guid.CreateVersion7(now),
                    BatchId = next.BatchId!.Value,
                    MessageId = id,
                    LabelsAdded = [names.Target],
                    LabelsRemoved = [names.Source],
                    LabelIdsBefore = before,
                    LabelIdsAfter = LabelChunks.After(before, [item.TargetLabelId!], [item.LabelId]),
                    CreatedAt = now,
                });
            }

            await db.SaveChangesAsync(t);
        }, ct);
        db.ChangeTracker.Clear();
        return (next, signal);
    }

    /// <summary>
    /// Sends the pending chunk, then stores the moved labels, notes the ids Gmail refused one by one and clears the chunk.
    /// When Gmail refuses the whole call (a 4xx: a label was deleted meanwhile), the item fails: a first send's log rows
    /// are deleted, a re-send's are kept (an earlier send may have reached Gmail, so they count towards the batch and stay
    /// undoable) and the chunk is cleared either way.
    /// Rate limits and a lost connection fail the job; a first send is reverted, a re-send stays pending.
    /// </summary>
    private async Task<(LabelPlanApplyCursor, JobSignal, string?)> SendAsync(
        JobContext ctx, LabelPlanApplyCursor cursor, LabelPlanItem item, bool resent, CancellationToken ct)
    {
        var ids = cursor.Pending!;
        var batchId = cursor.BatchId!.Value;
        string[] add = [item.TargetLabelId!];
        string[] remove = [item.LabelId];
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);
        var sent = false;
        try
        {
            try
            {
                await gmail.BatchModifyAsync(ids, add, remove, ct);
            }
            catch (GoogleApiException ex) when (LabelChunks.IsBadIdOrLabel(ex))
            {
                await LabelChunks.IsolateAsync(gmail, ids, add, remove, ex, refused, () => sent = true, ct);
                LabelChunks.ThrowIfCallRefused(ids, refused, ex);
            }
        }
        catch (Exception ex) when (!sent && LabelChunks.NothingChanged(ex) && (!resent || ItemError(ex) is not null))
        {
            LogChunkRefused(logger, ids.Length, ex);
            var cleared = resent ? await KeepAsync(ctx, cursor, item) : await RevertAsync(ctx, cursor, item);
            if (ItemError(ex) is { } error)
            {
                return (cleared, JobSignal.Continue, error);
            }

            throw;
        }

        string[] moved = [.. ids.Where(id => !refused.ContainsKey(id))];
        var done = cursor with { Pending = null };
        var signal = await ctx.CheckpointAsync(done, Progress(cursor, item.LabelName), async t =>
        {
            var now = time.GetUtcNow();
            var messages = await db.Messages.Where(m => moved.Contains(m.Id)).ToListAsync(t);
            foreach (var message in messages)
            {
                message.LabelIds = LabelChunks.After(message.LabelIds, add, remove);
                message.UpdatedAt = now;
            }

            await db.SaveChangesAsync(t);
            foreach (var reason in refused.GroupBy(r => r.Value, r => r.Key))
            {
                string[] refusedIds = [.. reason];
                await db.ActionLog.Where(l => l.BatchId == batchId && refusedIds.Contains(l.MessageId))
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.Note, reason.Key).SetProperty(l => l.LabelIdsAfter, l => l.LabelIdsBefore), t);
            }

            await db.ActionBatches.Where(b => b.Id == batchId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.MessageCount, b => b.MessageCount + moved.Length), t);
        }, ct);
        db.ChangeTracker.Clear();
        return (done, signal, null);
    }

    /// <summary>
    /// Undoes <see cref="PrepareAsync"/> for a chunk Gmail never saw: deletes its log rows (the newest of the batch for
    /// its ids) and the batch too when nothing else is in it, and clears the chunk.
    /// </summary>
    private async Task<LabelPlanApplyCursor> RevertAsync(JobContext ctx, LabelPlanApplyCursor cursor, LabelPlanItem item)
    {
        db.ChangeTracker.Clear();
        var ids = cursor.Pending!;
        var batchId = cursor.BatchId!.Value;
        var latest = await db.ActionLog.Where(l => l.BatchId == batchId && ids.Contains(l.MessageId)).MaxAsync(l => (DateTimeOffset?)l.CreatedAt);
        var rows = db.ActionLog.Where(l => l.BatchId == batchId && ids.Contains(l.MessageId) && l.CreatedAt == latest);
        var empty = await rows.CountAsync() == await db.ActionLog.CountAsync(l => l.BatchId == batchId);
        var cleared = cursor with { Pending = null, BatchId = empty ? null : batchId };
        await ctx.CheckpointAsync(cleared, Progress(cursor, item.LabelName), async t =>
        {
            await rows.ExecuteDeleteAsync(t);
            if (empty)
            {
                await db.ActionBatches.Where(b => b.Id == batchId).ExecuteDeleteAsync(t);
            }
        }, CancellationToken.None);
        return cleared;
    }

    /// <summary>Clears a re-sent chunk Gmail refused but keeps its log rows, counted in the batch so History can undo them.</summary>
    private async Task<LabelPlanApplyCursor> KeepAsync(JobContext ctx, LabelPlanApplyCursor cursor, LabelPlanItem item)
    {
        var count = cursor.Pending!.Length;
        var batchId = cursor.BatchId!.Value;
        var cleared = cursor with { Pending = null };
        await ctx.CheckpointAsync(cleared, Progress(cursor, item.LabelName), t =>
            db.ActionBatches.Where(b => b.Id == batchId).ExecuteUpdateAsync(s => s.SetProperty(b => b.MessageCount, b => b.MessageCount + count), t),
            CancellationToken.None);
        return cleared;
    }

    /// <summary>The source and target label names when the merge runs.</summary>
    private sealed record MergeNames(string Source, string Target);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail refused a label merge chunk of {Count} messages.")]
    private static partial void LogChunkRefused(ILogger logger, int count, Exception exception);
}
