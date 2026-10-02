using System.Net;
using GmailOrganiser.Analysis;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using Google;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

public sealed partial class ApplyActionsJob
{
    public const string NotFoundReason = LabelChunks.NotFoundReason;
    public const string RefusedReason = LabelChunks.RefusedReason;

    /// <summary>Refused re-sends of a pending chunk after which it is finalised from what Gmail shows.</summary>
    public const int MaxSendFailures = ActionDoneScanner.MaxSendFailures;

    /// <summary>
    /// Sends the pending chunk, then stores its result and clears it. Ids Gmail refuses one by one are skipped and
    /// their log rows removed. A failure leaves the chunk pending (its undo log kept) unless this run sent it for the
    /// first time and Gmail certainly changed nothing: then the chunk is reverted. A resent chunk is never reverted:
    /// Gmail's definitive refusals of it are counted, and the <see cref="MaxSendFailures"/>th finalises it from what
    /// Gmail shows (<see cref="ReconcileAsync"/>) and fails the job, so it can be cancelled, resumed or undone instead
    /// of blocking the account.
    /// </summary>
    /// <param name="resent">The chunk was pending when the run started, so an earlier send may have reached Gmail.</param>
    /// <returns>The new cursor, the checkpoint's signal and the total without the skipped messages.</returns>
    private async Task<(ApplyCursor Cursor, JobSignal Signal, int Total)> SendAsync(
        JobContext ctx, ApplyCursor cursor, bool resent, int total, CancellationToken ct)
    {
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);
        Exception? finalised = null;
        Dictionary<string, string[]>? real = null;
        string[] unchanged = [];
        if (cursor.Pending is { MessageIds.Length: > 0 } chunk && chunk.Add.Length + chunk.Remove.Length > 0)
        {
            var sent = new SendState();
            try
            {
                cursor = await SendChunkAsync(ctx, cursor, total, refused, sent, ct);
            }
            catch (Exception ex) when (!resent && !sent.Any && LabelChunks.NothingChanged(ex))
            {
                LogChunkReverted(logger, chunk.MessageIds.Length, ex);
                await RevertAsync(ctx, cursor, total);
                throw;
            }
            catch (GoogleApiException ex) when (resent && IsDefinitiveRefusal(ex))
            {
                if (!await CountResendFailureAsync(cursor.BatchId))
                {
                    throw;
                }

                // Gmail may or may not have applied it: what it shows now decides each message. A failed re-read
                // throws here, leaving the chunk pending and this attempt uncounted.
                cursor = ctx.ReadCursor<ApplyCursor>() ?? cursor;
                refused.Clear();
                (real, unchanged) = await ReconcileAsync(cursor.Pending!, refused, ct);
                LogChunkFinalised(logger, chunk.MessageIds.Length, MaxSendFailures, unchanged.Length, ex);
                finalised = ex;
            }
        }

        var pending = cursor.Pending!;
        var skips = await SkipsAsync(cursor.BatchId, refused, ct);
        var applied = pending.MessageIds.Where(id => !refused.ContainsKey(id) && !unchanged.Contains(id)).ToArray();
        string[] stored = [.. applied, .. unchanged];
        var done = cursor with
        {
            Pending = null,
            ChunksDone = cursor.ChunksDone + 1,
            MessagesDone = cursor.MessagesDone + applied.Length,
            Skipped = skips.Count == 0 ? cursor.Skipped : [.. cursor.Skipped ?? [], .. skips],
        };
        total -= refused.Count + unchanged.Length;
        var signal = await ctx.CheckpointAsync(done, Progress(done, total), async t =>
        {
            var now = time.GetUtcNow();
            await RevertRowsAsync(cursor.BatchId, [.. refused.Keys, .. unchanged], t);
            var gone = refused.Where(r => r.Value == NotFoundReason).Select(r => r.Key).ToArray();
            await db.Messages.Where(m => gone.Contains(m.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, true).SetProperty(m => m.UpdatedAt, now), t);

            var messages = await db.Messages.Where(m => stored.Contains(m.Id)).ToListAsync(t);
            foreach (var message in messages)
            {
                message.LabelIds = real?.GetValueOrDefault(message.Id) ?? After(message.LabelIds, pending.Add, pending.Remove);
                message.UpdatedAt = now;
            }

            await db.SaveChangesAsync(t);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE senders AS s SET applied_count = s.applied_count + c.n
                FROM (SELECT from_address, count(*)::int AS n FROM messages WHERE id = ANY({applied}) GROUP BY from_address) AS c
                WHERE s.address = c.from_address
                """, t);
            await db.ActionBatches.Where(b => b.Id == cursor.BatchId).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.MessageCount, b => b.MessageCount + applied.Length)
                .SetProperty(b => b.SendFailures, 0), t);
        }, ct);
        db.ChangeTracker.Clear();
        if (finalised is not null)
        {
            throw new JobRefusedException(
                $"Resending an interrupted chunk of {pending.MessageIds.Length} messages failed {MaxSendFailures} times. "
                + $"{applied.Length} that Gmail shows as changed are recorded as applied; {unchanged.Length} are approved again. "
                + "Resume to apply the rest, or cancel and undo the batch from History.");
        }

        return (done, signal, total);
    }

    /// <summary>
    /// A 4xx other than a rate limit: Gmail refused the call, so another identical re-send would be refused too.
    /// Rate limits, server errors, timeouts and a lost connection are retried by resuming and never counted.
    /// </summary>
    private static bool IsDefinitiveRefusal(GoogleApiException ex) =>
        ex.HttpStatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError
            and not HttpStatusCode.TooManyRequests;

    /// <summary>
    /// Counts a refused re-send of the batch's pending chunk. False while under <see cref="MaxSendFailures"/>; true
    /// (uncounted, as the store resets the count) when this attempt is the one that finalises the chunk.
    /// </summary>
    private async Task<bool> CountResendFailureAsync(Guid batchId)
    {
        var failures = await db.ActionBatches.Where(b => b.Id == batchId).Select(b => b.SendFailures).SingleAsync(CancellationToken.None);
        if (failures + 1 >= MaxSendFailures)
        {
            return true;
        }

        await db.ActionBatches.Where(b => b.Id == batchId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.SendFailures, b => b.SendFailures + 1), CancellationToken.None);
        return false;
    }

    /// <summary>
    /// Re-reads a chunk being finalised. Messages gone from Gmail go to <paramref name="refused"/> as not found; of
    /// the rest, those without the planned change are returned as unchanged (Gmail never applied them).
    /// </summary>
    /// <returns>Each remaining message's labels in Gmail, and the unchanged ids.</returns>
    private async Task<(Dictionary<string, string[]> Labels, string[] Unchanged)> ReconcileAsync(
        ApplyChunk chunk, Dictionary<string, string> refused, CancellationToken ct)
    {
        var metadata = await gmail.GetMessagesMetadataAsync(chunk.MessageIds, ct);
        var labels = metadata.ToDictionary(m => m.Id, m => m.LabelIds.ToArray(), StringComparer.Ordinal);
        foreach (var id in chunk.MessageIds.Where(id => !labels.ContainsKey(id)))
        {
            refused[id] = NotFoundReason;
        }

        string[] unchanged = [.. labels
            .Where(l => !chunk.Add.All(l.Value.Contains) || chunk.Remove.Any(l.Value.Contains))
            .Select(l => l.Key)];
        return (labels, unchanged);
    }

    /// <summary>
    /// One <c>batchModify</c>. On a 400 or 404 the labels are checked once (a label deleted in Gmail is created again
    /// and the chunk and its log rewritten to the new id), then the chunk is split until the ids Gmail refuses on their
    /// own are found; those go to <paramref name="refused"/> and the rest are sent. A single id's 404 means the message
    /// is gone, so it is skipped even when every id gets one. When Gmail answers 400 for every id of a chunk of several,
    /// the call itself is the problem and the failure is rethrown.
    /// </summary>
    private async Task<ApplyCursor> SendChunkAsync(
        JobContext ctx, ApplyCursor cursor, int total, Dictionary<string, string> refused, SendState sent, CancellationToken ct)
    {
        GoogleApiException failure;
        try
        {
            await SendPartAsync(cursor.Pending!, cursor.Pending!.MessageIds, sent, ct);
            return cursor;
        }
        catch (GoogleApiException ex) when (LabelChunks.IsBadIdOrLabel(ex))
        {
            failure = ex;
        }

        if (await ReResolveLabelsAsync(ctx, cursor, total, ct) is { } rewritten)
        {
            cursor = rewritten;
            try
            {
                await SendPartAsync(cursor.Pending!, cursor.Pending!.MessageIds, sent, ct);
                return cursor;
            }
            catch (GoogleApiException ex) when (LabelChunks.IsBadIdOrLabel(ex))
            {
                failure = ex;
            }
        }

        var pending = cursor.Pending!;
        var ids = pending.MessageIds;
        await LabelChunks.IsolateAsync(gmail, ids, pending.Add, pending.Remove, failure, refused, () => sent.Any = true, ct);
        LabelChunks.ThrowIfCallRefused(ids, refused, failure);
        LogIdsRefused(logger, refused.Count, ids.Length);
        return cursor;
    }

    private async Task SendPartAsync(ApplyChunk chunk, string[] ids, SendState sent, CancellationToken ct)
    {
        await gmail.BatchModifyAsync(ids, chunk.Add, chunk.Remove, ct);
        sent.Any = true;
    }

    /// <summary>
    /// Reloads the labels; when one the chunk adds no longer exists, creates its path again (from the log's display
    /// names) and checkpoints the chunk and its log rows with the new id. Null when every label still exists.
    /// </summary>
    private async Task<ApplyCursor?> ReResolveLabelsAsync(JobContext ctx, ApplyCursor cursor, int total, CancellationToken ct)
    {
        var pending = cursor.Pending!;
        var existing = (await catalog.RefreshAsync(ct)).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var missing = Enumerable.Range(0, pending.Add.Length).Where(i => !existing.Contains(pending.Add[i])).ToList();
        if (missing.Count == 0)
        {
            return null;
        }

        var log = await db.ActionLog.AsNoTracking()
            .FirstAsync(l => l.BatchId == cursor.BatchId && l.MessageId == pending.MessageIds[0], ct);
        if (log.LabelsAdded.Length != pending.Add.Length)
        {
            return null;
        }

        var paths = missing.ConvertAll(i => log.LabelsAdded[i]);
        var resolved = await labels.EnsureAsync(paths, (label, _) => RecordCreatedAsync(cursor.BatchId, label), ct);
        var renamed = missing.ToDictionary(i => pending.Add[i], i => resolved[log.LabelsAdded[i]], StringComparer.Ordinal);
        // Same order, so the log's display names still line up with the ids.
        var add = pending.Add.Select(id => renamed.GetValueOrDefault(id, id)).ToArray();
        var next = cursor with { Pending = pending with { Add = add } };
        await ctx.CheckpointAsync(next, Progress(cursor, total), async t =>
        {
            var rows = await db.ActionLog
                .Where(l => l.BatchId == cursor.BatchId && pending.MessageIds.Contains(l.MessageId) && l.UndoneByBatchId == null)
                .ToListAsync(t);
            foreach (var row in rows)
            {
                row.LabelIdsAfter = [.. row.LabelIdsAfter.Select(id => renamed.GetValueOrDefault(id, id))];
            }

            await db.SaveChangesAsync(t);
        }, ct);
        db.ChangeTracker.Clear();
        return next;
    }

    /// <summary>The skip records of the refused ids, from their log rows.</summary>
    private async Task<List<ApplySkip>> SkipsAsync(Guid batchId, Dictionary<string, string> refused, CancellationToken ct)
    {
        if (refused.Count == 0)
        {
            return [];
        }

        var ids = refused.Keys.ToArray();
        var rows = await db.ActionLog.AsNoTracking()
            .Where(l => l.BatchId == batchId && ids.Contains(l.MessageId) && l.SuggestionId != null)
            .Select(l => new { l.MessageId, l.SuggestionId })
            .ToListAsync(ct);
        return rows.ConvertAll(r => new ApplySkip(r.SuggestionId!.Value, r.MessageId, refused[r.MessageId]));
    }

    /// <summary>Undoes <see cref="PrepareAsync"/> for a chunk Gmail never saw: log rows deleted, suggestions approved again.</summary>
    private async Task RevertAsync(JobContext ctx, ApplyCursor cursor, int total)
    {
        db.ChangeTracker.Clear();
        var pending = cursor.Pending!;
        await ctx.CheckpointAsync(
            cursor with { Pending = null }, Progress(cursor, total), t => RevertRowsAsync(cursor.BatchId, pending.MessageIds, t),
            CancellationToken.None);
        db.ChangeTracker.Clear();
    }

    /// <summary>Deletes the batch's log rows of <paramref name="messageIds"/> and sets their suggestions approved again.</summary>
    private async Task RevertRowsAsync(Guid batchId, string[] messageIds, CancellationToken ct)
    {
        if (messageIds.Length == 0)
        {
            return;
        }

        var log = await db.ActionLog.Where(l => l.BatchId == batchId && messageIds.Contains(l.MessageId)).ToListAsync(ct);
        var suggestionIds = log.Where(l => l.SuggestionId is not null).Select(l => l.SuggestionId!.Value).ToList();
        var suggestions = await db.Suggestions
            .Where(s => suggestionIds.Contains(s.Id) && s.Status == SuggestionStatus.Applied)
            .ToListAsync(ct);
        var messages = await db.Messages.Where(m => messageIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        foreach (var suggestion in suggestions)
        {
            // Keeps the approval time, so the batch still covers the suggestion on resume.
            suggestion.SetStatus(SuggestionStatus.Approved, messages[suggestion.MessageId], suggestion.DecidedAt ?? time.GetUtcNow());
        }

        db.ActionLog.RemoveRange(log);
        await db.SaveChangesAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail refused a chunk of {Count} messages before changing any; the chunk was reverted.")]
    private static partial void LogChunkReverted(ILogger logger, int count, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A resent chunk of {Count} messages failed {Failures} times; it is finalised from Gmail, {Unchanged} unchanged.")]
    private static partial void LogChunkFinalised(ILogger logger, int count, int failures, int unchanged, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail refused {Refused} of {Count} messages in a chunk; they are skipped.")]
    private static partial void LogIdsRefused(ILogger logger, int refused, int count);

    /// <summary>Whether any part of the chunk reached Gmail in this run.</summary>
    private sealed class SendState
    {
        public bool Any { get; set; }
    }
}
