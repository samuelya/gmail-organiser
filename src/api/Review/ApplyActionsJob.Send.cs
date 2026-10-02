using System.Net;
using GmailOrganiser.Analysis;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using Google;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

public sealed partial class ApplyActionsJob
{
    public const string NotFoundReason = "not found in Gmail";
    public const string RefusedReason = "refused by Gmail";

    /// <summary>
    /// Sends the pending chunk, then stores its result and clears it. Ids Gmail refuses one by one are skipped and
    /// their log rows removed. A failure leaves the chunk pending (its undo log kept) unless this run sent it for the
    /// first time and Gmail certainly changed nothing: then the chunk is reverted.
    /// </summary>
    /// <param name="resent">The chunk was pending when the run started, so an earlier send may have reached Gmail.</param>
    /// <returns>The new cursor, the checkpoint's signal and the total without the skipped messages.</returns>
    private async Task<(ApplyCursor Cursor, JobSignal Signal, int Total)> SendAsync(
        JobContext ctx, ApplyCursor cursor, bool resent, int total, CancellationToken ct)
    {
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);
        if (cursor.Pending is { MessageIds.Length: > 0 } chunk && chunk.Add.Length + chunk.Remove.Length > 0)
        {
            var sent = new SendState();
            try
            {
                cursor = await SendChunkAsync(ctx, cursor, total, refused, sent, ct);
            }
            catch (Exception ex) when (!resent && !sent.Any && NothingChanged(ex))
            {
                LogChunkReverted(logger, chunk.MessageIds.Length, ex);
                await RevertAsync(ctx, cursor, total);
                throw;
            }
        }

        var pending = cursor.Pending!;
        var skips = await SkipsAsync(cursor.BatchId, refused, ct);
        var applied = pending.MessageIds.Where(id => !refused.ContainsKey(id)).ToArray();
        var done = cursor with
        {
            Pending = null,
            ChunksDone = cursor.ChunksDone + 1,
            MessagesDone = cursor.MessagesDone + applied.Length,
            Skipped = skips.Count == 0 ? cursor.Skipped : [.. cursor.Skipped ?? [], .. skips],
        };
        total -= refused.Count;
        var signal = await ctx.CheckpointAsync(done, Progress(done, total), async t =>
        {
            var now = time.GetUtcNow();
            await RevertRowsAsync(cursor.BatchId, [.. refused.Keys], t);
            var gone = refused.Where(r => r.Value == NotFoundReason).Select(r => r.Key).ToArray();
            await db.Messages.Where(m => gone.Contains(m.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, true).SetProperty(m => m.UpdatedAt, now), t);

            var messages = await db.Messages.Where(m => applied.Contains(m.Id)).ToListAsync(t);
            foreach (var message in messages)
            {
                message.LabelIds = After(message.LabelIds, pending.Add, pending.Remove);
                message.UpdatedAt = now;
            }

            await db.SaveChangesAsync(t);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE senders AS s SET applied_count = s.applied_count + c.n
                FROM (SELECT from_address, count(*)::int AS n FROM messages WHERE id = ANY({applied}) GROUP BY from_address) AS c
                WHERE s.address = c.from_address
                """, t);
            await db.ActionBatches.Where(b => b.Id == cursor.BatchId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.MessageCount, b => b.MessageCount + applied.Length), t);
        }, ct);
        db.ChangeTracker.Clear();
        return (done, signal, total);
    }

    /// <summary>
    /// One <c>batchModify</c>. On a 400 or 404 the labels are checked once (a label deleted in Gmail is created again
    /// and the chunk and its log rewritten to the new id), then the chunk is split until the ids Gmail refuses on their
    /// own are found; those go to <paramref name="refused"/> and the rest are sent. When Gmail refuses every id of a
    /// chunk of several, the call itself is the problem and the failure is rethrown.
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
        catch (GoogleApiException ex) when (IsBadIdOrLabel(ex))
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
            catch (GoogleApiException ex) when (IsBadIdOrLabel(ex))
            {
                failure = ex;
            }
        }

        var ids = cursor.Pending!.MessageIds;
        await IsolateAsync(cursor.Pending!, ids, failure, refused, sent, ct);
        if (ids.Length > 1 && refused.Count == ids.Length)
        {
            // Not a few bad ids but the call itself: fail the chunk rather than skip every message.
            refused.Clear();
            throw failure;
        }

        LogIdsRefused(logger, refused.Count, ids.Length);
        return cursor;
    }

    /// <summary><paramref name="ids"/> failed together with <paramref name="failure"/>: sends each half, splitting again on failure.</summary>
    private async Task IsolateAsync(
        ApplyChunk chunk, string[] ids, GoogleApiException failure, Dictionary<string, string> refused, SendState sent, CancellationToken ct)
    {
        if (ids.Length == 1)
        {
            refused[ids[0]] = failure.HttpStatusCode == HttpStatusCode.NotFound ? NotFoundReason : RefusedReason;
            return;
        }

        foreach (var half in new[] { ids[..(ids.Length / 2)], ids[(ids.Length / 2)..] })
        {
            try
            {
                await SendPartAsync(chunk, half, sent, ct);
            }
            catch (GoogleApiException ex) when (IsBadIdOrLabel(ex))
            {
                await IsolateAsync(chunk, half, ex, refused, sent, ct);
            }
        }
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

    /// <summary>Gmail refused the call before changing anything: rate-limited on every attempt, not connected, or a 4xx.</summary>
    private static bool NothingChanged(Exception ex) => ex
        is GmailRateLimitedException
        or GmailNotConnectedException
        or ArgumentException
        or GoogleApiException { HttpStatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError };

    /// <summary>What Gmail answers for an unknown message or label id.</summary>
    private static bool IsBadIdOrLabel(GoogleApiException ex) =>
        ex.HttpStatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail refused a chunk of {Count} messages before changing any; the chunk was reverted.")]
    private static partial void LogChunkReverted(ILogger logger, int count, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail refused {Refused} of {Count} messages in a chunk; they are skipped.")]
    private static partial void LogIdsRefused(ILogger logger, int refused, int count);

    /// <summary>Whether any part of the chunk reached Gmail in this run.</summary>
    private sealed class SendState
    {
        public bool Any { get; set; }
    }
}
