using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using Google;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>What Gmail did with a sent chunk: ids it changed, ids it no longer has (404) and ids it refused one by one.</summary>
public sealed record ChunkResult(string[] Changed, string[] Gone, string[] Rejected);

/// <summary>
/// The Gmail-mutation half the chunked action jobs share (<see cref="CleanUp.CleanUpActionsJob"/>,
/// <see cref="Senders.SenderArchiveJob"/>): each chunk is two transactions around one <c>batchModify</c>.
/// <see cref="PrepareAsync"/> locks and re-checks the messages, writes the undo log and checkpoints the chunk as pending;
/// <see cref="SendAsync"/> sends it and <see cref="StoreAsync"/> stores its result and clears it. A pending chunk is
/// resent on resume, which Gmail treats as a no-op for what it already applied. The cursor stays the job's own.
/// </summary>
public sealed partial class ChunkSender(AppDbContext db, IGmailClient gmail, TimeProvider time, ILogger logger, string job)
{
    /// <summary>
    /// Locks <paramref name="ids"/>, asks <paramref name="fits"/> whether every one still takes the change (state can
    /// change between the click and the send), writes the undo log and checkpoints <paramref name="next"/>, in one
    /// transaction. False when a message is missing or no longer fits: nothing was written, the caller replans. Any
    /// other exception from <paramref name="fits"/> propagates with nothing written.
    /// </summary>
    /// <param name="names">Label names for the log by id; ids without a name (or no map) are logged as they are.</param>
    public async Task<bool> PrepareAsync<TCursor>(
        JobContext ctx,
        TCursor next,
        JobProgress progress,
        Guid batchId,
        string[] ids,
        string[] add,
        string[] remove,
        IReadOnlyDictionary<string, string>? names,
        Func<List<MessageRow>, CancellationToken, Task<bool>> fits,
        CancellationToken ct)
    {
        try
        {
            await ctx.CheckpointAsync(next, progress, async t =>
            {
                var messages = await db.Messages
                    .FromSql($"SELECT * FROM messages WHERE id = ANY({ids}) ORDER BY id FOR UPDATE")
                    .ToListAsync(t);
                if (messages.Count != ids.Length || !await fits(messages, t))
                {
                    throw new PlanChangedException();
                }

                var now = time.GetUtcNow();
                foreach (var message in messages)
                {
                    db.ActionLog.Add(new ActionLogRow
                    {
                        Id = Guid.CreateVersion7(now),
                        BatchId = batchId,
                        MessageId = message.Id,
                        LabelsAdded = [.. add.Select(Name)],
                        LabelsRemoved = [.. remove.Select(Name)],
                        LabelIdsBefore = message.LabelIds,
                        LabelIdsAfter = LabelChunks.After(message.LabelIds, add, remove),
                        CreatedAt = now,
                    });
                }

                await db.SaveChangesAsync(t);
            }, ct);
        }
        catch (PlanChangedException)
        {
            db.ChangeTracker.Clear();
            return false;
        }

        db.ChangeTracker.Clear();
        return true;

        string Name(string id) => names?.GetValueOrDefault(id, id) ?? id;
    }

    /// <summary>
    /// Sends the pending chunk. Ids Gmail answers 404 for are gone, ids it refuses one by one are rejected. A failure
    /// leaves the chunk pending unless this run sent it for the first time (<paramref name="resent"/> false) and Gmail
    /// certainly changed nothing: then <paramref name="reverted"/> (the cursor without the pending chunk) is
    /// checkpointed with the chunk's log rows deleted, and the failure rethrown.
    /// </summary>
    public async Task<ChunkResult> SendAsync<TCursor>(
        JobContext ctx,
        Guid batchId,
        string[] ids,
        string[] add,
        string[] remove,
        bool resent,
        TCursor reverted,
        JobProgress progress,
        CancellationToken ct)
    {
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ids.Length > 0 && add.Length + remove.Length > 0)
        {
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
            catch (Exception ex) when (!resent && !sent && LabelChunks.NothingChanged(ex))
            {
                LogChunkReverted(logger, job, ids.Length, ex);
                await RevertAsync(ctx, batchId, ids, reverted, progress);
                throw;
            }
        }

        return new(
            [.. ids.Where(id => !refused.ContainsKey(id))],
            [.. refused.Where(r => r.Value == LabelChunks.NotFoundReason).Select(r => r.Key)],
            [.. refused.Where(r => r.Value != LabelChunks.NotFoundReason).Select(r => r.Key)]);
    }

    /// <summary>
    /// Checkpoints <paramref name="done"/> (the cursor with the chunk cleared) and, in the same transaction, deletes the
    /// log rows of ids Gmail did not change, marks the gone ids deleted, stores the changed ids' labels (then
    /// <paramref name="changedRow"/> on each) and adds the changed count to the batch. <paramref name="afterSave"/> runs
    /// after the labels are saved, for what reads them (sender stats).
    /// </summary>
    public async Task<JobSignal> StoreAsync<TCursor>(
        JobContext ctx,
        TCursor done,
        JobProgress progress,
        Guid batchId,
        string[] add,
        string[] remove,
        ChunkResult result,
        Action<MessageRow>? changedRow,
        Func<CancellationToken, Task>? afterSave,
        CancellationToken ct)
    {
        var (changed, gone, rejected) = result;
        var signal = await ctx.CheckpointAsync(done, progress, async t =>
        {
            var now = time.GetUtcNow();
            string[] unlogged = [.. gone, .. rejected];
            await db.ActionLog.Where(l => l.BatchId == batchId && unlogged.Contains(l.MessageId)).ExecuteDeleteAsync(t);
            await db.Messages.Where(m => gone.Contains(m.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, true).SetProperty(m => m.UpdatedAt, now), t);

            var messages = await db.Messages.Where(m => changed.Contains(m.Id)).ToListAsync(t);
            foreach (var message in messages)
            {
                message.LabelIds = LabelChunks.After(message.LabelIds, add, remove);
                changedRow?.Invoke(message);
                message.UpdatedAt = now;
            }

            await db.SaveChangesAsync(t);
            if (afterSave is not null)
            {
                await afterSave(t);
            }

            await db.ActionBatches.Where(b => b.Id == batchId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.MessageCount, b => b.MessageCount + changed.Length), t);
        }, ct);
        db.ChangeTracker.Clear();
        return signal;
    }

    /// <summary>The rejected ids added to the cursor's skipped ones; the same array when none were rejected.</summary>
    public static string[]? Skipped(string[]? skipped, ChunkResult result) =>
        result.Rejected.Length == 0 ? skipped : [.. skipped ?? [], .. result.Rejected];

    /// <summary>"<paramref name="verb"/> n of total messages" and every skip with its reason, protected first.</summary>
    public static string Message(string verb, int messagesDone, int total, int skippedProtected, string protectedReason, int gone, string[]? skipped)
    {
        var reasons = new List<string>();
        if (skippedProtected > 0)
        {
            reasons.Add($"{skippedProtected} {protectedReason}");
        }

        if (gone > 0)
        {
            reasons.Add($"{gone} {LabelChunks.NotFoundReason}");
        }

        if (skipped is { Length: > 0 })
        {
            reasons.Add($"{skipped.Length} {LabelChunks.RefusedReason}");
        }

        return $"{verb} {messagesDone} of {total} messages{(reasons.Count > 0 ? $"; skipped {string.Join(", ", reasons)}" : "")}";
    }

    /// <summary>Undoes <see cref="PrepareAsync"/> for a chunk Gmail never saw: its log rows are deleted.</summary>
    private async Task RevertAsync<TCursor>(JobContext ctx, Guid batchId, string[] ids, TCursor reverted, JobProgress progress)
    {
        db.ChangeTracker.Clear();
        await ctx.CheckpointAsync(
            reverted,
            progress,
            t => db.ActionLog.Where(l => l.BatchId == batchId && ids.Contains(l.MessageId)).ExecuteDeleteAsync(t),
            CancellationToken.None);
        db.ChangeTracker.Clear();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail refused a {Job} chunk of {Count} messages before changing any; the chunk was reverted.")]
    private static partial void LogChunkReverted(ILogger logger, string job, int count, Exception exception);

    private sealed class PlanChangedException : Exception;
}
