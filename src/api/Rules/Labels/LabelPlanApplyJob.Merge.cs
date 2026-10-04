using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using Google;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Rules.Labels;

public sealed partial class LabelPlanApplyJob
{
    public static string DescribeMerge(LabelPlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return $"Merged label {item.LabelName} into {item.TargetLabelName}";
    }

    /// <summary>
    /// Moves every message of the source label to the target: a pending chunk is re-sent first, then the label's message
    /// ids are listed in full (nothing changes while paging) and moved in chunks, each logged in the item's
    /// <see cref="ActionKind.LabelMerge"/> batch before its <c>batchModify</c>, as <see cref="UndoActionsJob"/> does. Then
    /// each affected filter is retargeted. Both steps re-run safely: moved mail no longer lists, a retargeted filter is
    /// found by its <c>restored_from</c>.
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
        if (item.TargetLabelId is not { } target || !labels.Any(l => l.Id == item.LabelId) || !labels.Any(l => l.Id == target))
        {
            return (cursor, JobSignal.Continue, LabelPlanItemStatus.Failed, MissingError);
        }

        foreach (var chunk in (await ListAsync(item.LabelId, ct)).Chunk(gmailOptions.Value.BatchModifyMaxIds))
        {
            cursor = await PrepareAsync(ctx, cursor, item, chunk, ct);
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
                var result = await filters.RetargetAsync(filterId, item.LabelId, target, ct);
                if (result.Outcome == FilterOutcome.Conflict)
                {
                    errors.Add($"filter {filterId}: {result.Detail}");
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

    /// <summary>Every message id carrying <paramref name="labelId"/>, read page by page before anything changes.</summary>
    private async Task<List<string>> ListAsync(string labelId, CancellationToken ct)
    {
        var ids = new List<string>();
        string? token = null;
        do
        {
            var page = await gmail.ListMessageIdsAsync(new MessageListQuery(null, [labelId], token, MessageListQuery.MaxPageSize), ct);
            ids.AddRange(page.Messages.Select(m => m.Id));
            token = page.NextPageToken;
        }
        while (token is not null);

        return [.. ids.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Writes the chunk's log rows (and the item's batch with the first chunk) and checkpoints it as pending, in one
    /// transaction. The labels before are the stored ones, or Gmail's for mail the app never stored, plus the source.
    /// </summary>
    private async Task<LabelPlanApplyCursor> PrepareAsync(
        JobContext ctx, LabelPlanApplyCursor cursor, LabelPlanItem item, string[] chunk, CancellationToken ct)
    {
        var stored = await db.Messages.AsNoTracking()
            .Where(m => chunk.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.LabelIds, StringComparer.Ordinal, ct);
        string[] missing = [.. chunk.Where(id => !stored.ContainsKey(id))];
        var read = missing.Length == 0
            ? []
            : (await gmail.GetMessagesLabelsAsync(missing, ct)).ToDictionary(m => m.Id, m => m.LabelIds.ToArray(), StringComparer.Ordinal);

        var now = time.GetUtcNow();
        var next = cursor with { BatchId = cursor.BatchId ?? Guid.CreateVersion7(now), Pending = chunk };
        await ctx.CheckpointAsync(next, Progress(cursor, item.LabelName), async t =>
        {
            if (cursor.BatchId is null)
            {
                db.ActionBatches.Add(new ActionBatchRow
                {
                    Id = next.BatchId!.Value,
                    Kind = ActionKind.LabelMerge,
                    Description = DescribeMerge(item),
                    JobId = ctx.JobId,
                    CreatedAt = now,
                });
            }

            foreach (var id in chunk)
            {
                string[] before = [.. (stored.GetValueOrDefault(id) ?? read.GetValueOrDefault(id) ?? []).Append(item.LabelId).Distinct(StringComparer.Ordinal)];
                db.ActionLog.Add(new ActionLogRow
                {
                    Id = Guid.CreateVersion7(now),
                    BatchId = next.BatchId!.Value,
                    MessageId = id,
                    LabelsAdded = [item.TargetLabelName ?? item.TargetLabelId!],
                    LabelsRemoved = [item.LabelName],
                    LabelIdsBefore = before,
                    LabelIdsAfter = LabelChunks.After(before, [item.TargetLabelId!], [item.LabelId]),
                    CreatedAt = now,
                });
            }

            await db.SaveChangesAsync(t);
        }, ct);
        db.ChangeTracker.Clear();
        return next;
    }

    /// <summary>
    /// Sends the pending chunk, then stores the moved labels, notes the ids Gmail refused one by one and clears the chunk.
    /// When Gmail refuses the whole call (a 4xx: a label was deleted meanwhile), the item fails: a first send's log rows
    /// are deleted, a re-send's are kept (an earlier send may have reached Gmail) and the chunk is cleared either way.
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
            var cleared = resent ? cursor with { Pending = null } : await RevertAsync(ctx, cursor, item);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail refused a label merge chunk of {Count} messages.")]
    private static partial void LogChunkRefused(ILogger logger, int count, Exception exception);
}
