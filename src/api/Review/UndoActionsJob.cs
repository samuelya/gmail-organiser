using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Senders;
using Google;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Review;

/// <param name="BatchId">The undo batch the inverse log rows belong to.</param>
/// <param name="UndoOf">The batch being reverted.</param>
/// <param name="Gone">Rows skipped because their message is gone from Gmail.</param>
/// <param name="Pending">The chunk whose inverse log is written and whose <c>batchModify</c> may have been sent.</param>
/// <param name="Skipped">Original log rows Gmail refused one by one; left not undone and not retried by this job.</param>
public sealed record UndoCursor(
    Guid BatchId,
    Guid UndoOf,
    int ChunksDone = 0,
    int MessagesDone = 0,
    int Gone = 0,
    UndoChunk? Pending = null,
    Guid[]? Skipped = null);

/// <summary>One <c>batchModify</c> reverting the original log rows <see cref="LogIds"/>; <see cref="Gone"/> chunks skip Gmail.</summary>
/// <param name="Deleted">Label ids the inverse names but Gmail no longer has: never sent, stripped from the stored labels.</param>
public sealed record UndoChunk(
    Guid[] LogIds, string[] MessageIds, string[] Add, string[] Remove, bool Gone = false, string[]? Deleted = null)
{
    /// <summary>The labels to take off the stored message: <see cref="Remove"/> and <see cref="Deleted"/>.</summary>
    public string[] StoredRemove => [.. Remove, .. Deleted ?? []];
}

/// <summary>
/// Reverts a History batch (DESIGN §3.6) from its <c>action_log</c>: per chunk of rows not yet undone, with identical
/// inverse label sets, adds what the batch removed and removes what it added. Like <see cref="ApplyActionsJob"/>, each
/// chunk is two transactions around the Gmail call: the first locks the original rows, writes the inverse rows and
/// marks the originals undone, checkpointing the chunk as pending; the second restores the stored labels, suggestions
/// and counts. Labels the batch created are kept; the History detail lists them for the user to remove by hand.
/// </summary>
public sealed partial class UndoActionsJob(
    AppDbContext db,
    IGmailClient gmail,
    LabelCatalog catalog,
    SenderStatsUpdater senders,
    IOptions<GmailOptions> gmailOptions,
    TimeProvider time,
    ILogger<UndoActionsJob> logger) : IJobHandler, IJobCancelHook
{
    public const string JobType = ReviewJobTypes.Undo;
    public const string Queue = JobQueues.Apply;
    public const string GoneNote = "message gone";
    public const string LabelDeletedNote = "label deleted in Gmail";
    private const string Trash = MailboxFetchJob.TrashLabelId;

    public string Type => JobType;

    public static string Describe(string original) => $"Undo: {original}";

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<UndoCursor>() ?? throw new JobRefusedException("The undo job has no batch.");
        if (await db.ActionBatches.CountAsync(b => b.Id == cursor.BatchId || b.Id == cursor.UndoOf, ct) != 2)
        {
            throw new JobRefusedException("The action batch no longer exists.");
        }

        var labels = await catalog.RefreshAsync(ct);
        var plan = await PlanAsync(cursor, labels, ct);
        var total = Done(cursor) + (cursor.Pending?.LogIds.Length ?? 0) + plan.Sum(c => c.LogIds.Length);
        if (cursor.Pending is not null)
        {
            cursor = await DropDeletedLabelsAsync(ctx, cursor, labels, total, ct);
            (cursor, var signal, total) = await SendAsync(ctx, cursor, resent: true, total, ct);
            if (signal != JobSignal.Continue)
            {
                return;
            }
        }

        while (true)
        {
            var replan = false;
            foreach (var chunk in plan)
            {
                if (await PrepareAsync(ctx, cursor, chunk, labels, total, ct) is not { } prepared)
                {
                    replan = true;
                    break;
                }

                (cursor, var signal, total) = await SendAsync(ctx, prepared, resent: false, total, ct);
                if (signal != JobSignal.Continue)
                {
                    return;
                }
            }

            if (!replan)
            {
                break;
            }

            plan = await PlanAsync(cursor, labels, ct);
            total = Done(cursor) + plan.Sum(c => c.LogIds.Length);
        }

        var complete = !await NotUndone(cursor.UndoOf).AnyAsync(ct);
        await ctx.CompleteAsync(cursor, Progress(cursor, total), async t =>
        {
            if (complete)
            {
                var now = time.GetUtcNow();
                await db.ActionBatches.Where(b => b.Id == cursor.UndoOf)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.UndoneAt, now), t);
            }
        }, ct);
    }

    /// <summary>A chunk that may have reached Gmail must be finished by a resume, not cancelled.</summary>
    public bool AllowsCancel(string? cursor) =>
        cursor is null || JsonSerializer.Deserialize<UndoCursor>(cursor, JobRow.Json)?.Pending is null;

    /// <summary>Nothing to follow up: rows not yet undone stay undoable by a new undo.</summary>
    public Task CancelledAsync(string? cursor, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The batch's log rows not yet undone.</summary>
    private IQueryable<ActionLogRow> NotUndone(Guid batchId) =>
        db.ActionLog.Where(l => l.BatchId == batchId && l.UndoneByBatchId == null);

    /// <summary>
    /// Chunks of the rows still to undo, in log order. The inverse comes from the stored label ids and keeps only
    /// labels that still exist (the others go to <see cref="UndoChunk.Deleted"/>); rows whose stored message is gone form their own chunks, which never reach Gmail.
    /// </summary>
    private async Task<List<UndoChunk>> PlanAsync(UndoCursor cursor, IReadOnlyList<GmailLabel> labels, CancellationToken ct)
    {
        var skipped = cursor.Skipped ?? [];
        var existing = labels.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var rows = await NotUndone(cursor.UndoOf)
            .Where(l => !skipped.Contains(l.Id))
            .OrderBy(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.MessageId,
                l.LabelIdsBefore,
                l.LabelIdsAfter,
                // Mail the app never stored (a label merge moves unfetched mail too) is reverted, not gone.
                Gone = db.Messages.Any(m => m.Id == l.MessageId && m.DeletedInGmail && !m.LabelIds.Contains(Trash)),
            })
            .AsNoTracking()
            .ToListAsync(ct);
        var cap = gmailOptions.Value.BatchModifyMaxIds;
        var gone = rows.Where(r => r.Gone).Chunk(cap)
            .Select(c => new UndoChunk([.. c.Select(r => r.Id)], [.. c.Select(r => r.MessageId)], [], [], Gone: true));
        var inverse = rows.Where(r => !r.Gone).Select(r => (r.Id, r.MessageId,
            Add: Inverse(r.LabelIdsBefore, r.LabelIdsAfter, existing), Remove: Inverse(r.LabelIdsAfter, r.LabelIdsBefore, existing)));
        var deleted = rows.Where(r => !r.Gone)
            .SelectMany(r => r.LabelIdsBefore.Concat(r.LabelIdsAfter))
            .Where(id => !existing.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var send = LabelChunks.Group(inverse, r => r.Add, r => r.Remove, cap)
            .Select(c => new UndoChunk(
                [.. c.Items.Select(r => r.Id)], [.. c.Items.Select(r => r.MessageId)], c.Add, c.Remove,
                Deleted: deleted.Length == 0 ? null : LabelChunks.Sorted(deleted)));
        return [.. gone, .. send];

        static string[] Inverse(string[] from, string[] minus, HashSet<string> existing) =>
            [.. from.Except(minus, StringComparer.Ordinal).Where(existing.Contains)];
    }

    /// <summary>
    /// Locks the chunk's original rows, writes their inverse rows under the undo batch and marks them undone, and
    /// checkpoints the chunk as pending, in one transaction. Null when a row was undone meanwhile: the caller replans.
    /// </summary>
    private async Task<UndoCursor?> PrepareAsync(
        JobContext ctx, UndoCursor cursor, UndoChunk chunk, IReadOnlyList<GmailLabel> labels, int total, CancellationToken ct)
    {
        var next = cursor with { Pending = chunk };
        var names = labels.ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
        try
        {
            await ctx.CheckpointAsync(next, Progress(cursor, total), async t =>
            {
                var ids = chunk.LogIds;
                var originals = await db.ActionLog
                    .FromSql($"SELECT * FROM action_log WHERE id = ANY({ids}) ORDER BY id FOR UPDATE")
                    .ToListAsync(t);
                var messages = await db.Messages.AsNoTracking()
                    .Where(m => chunk.MessageIds.Contains(m.Id))
                    .ToDictionaryAsync(m => m.Id, StringComparer.Ordinal, t);
                if (originals.Count != ids.Length
                    || originals.Any(o => o.UndoneByBatchId is not null)
                    || (!chunk.Gone && chunk.MessageIds.Any(id => messages.GetValueOrDefault(id) is { } m && Gone(m))))
                {
                    throw new PlanChangedException();
                }

                var now = time.GetUtcNow();
                foreach (var original in originals)
                {
                    var before = messages.GetValueOrDefault(original.MessageId)?.LabelIds ?? original.LabelIdsAfter;
                    db.ActionLog.Add(new ActionLogRow
                    {
                        Id = Guid.CreateVersion7(now),
                        BatchId = cursor.BatchId,
                        MessageId = original.MessageId,
                        SuggestionId = original.SuggestionId,
                        LabelsAdded = [.. chunk.Add.Select(id => names.GetValueOrDefault(id, id))],
                        LabelsRemoved = [.. chunk.Remove.Select(id => names.GetValueOrDefault(id, id))],
                        LabelIdsBefore = before,
                        LabelIdsAfter = chunk.Gone ? before : LabelChunks.After(before, chunk.Add, chunk.StoredRemove),
                        Note = chunk.Gone ? GoneNote : null,
                        CreatedAt = now,
                    });
                    original.UndoneByBatchId = cursor.BatchId;
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

    /// <summary>
    /// Drops from the pending chunk the labels deleted in Gmail since it was prepared, so the resend isn't refused: a
    /// label to remove is gone already, a label to re-add is skipped with <see cref="LabelDeletedNote"/>; both move to
    /// <see cref="UndoChunk.Deleted"/>. Rewrites the chunk and its inverse rows in one transaction; unchanged when every
    /// label still exists.
    /// </summary>
    private async Task<UndoCursor> DropDeletedLabelsAsync(
        JobContext ctx, UndoCursor cursor, IReadOnlyList<GmailLabel> labels, int total, CancellationToken ct)
    {
        var pending = cursor.Pending!;
        var existing = labels.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var keepAdd = Array.ConvertAll(pending.Add, existing.Contains);
        var keepRemove = Array.ConvertAll(pending.Remove, existing.Contains);
        var addDropped = keepAdd.Contains(false);
        if (pending.Gone || (!addDropped && !keepRemove.Contains(false)))
        {
            return cursor;
        }

        var chunk = pending with
        {
            Add = Keep(pending.Add, keepAdd),
            Remove = Keep(pending.Remove, keepRemove),
            Deleted = LabelChunks.Sorted(pending.Add.Concat(pending.Remove).Where(id => !existing.Contains(id))
                .Concat(pending.Deleted ?? []).Distinct(StringComparer.Ordinal)),
        };
        var next = cursor with { Pending = chunk };
        await ctx.CheckpointAsync(next, Progress(cursor, total), async t =>
        {
            var rows = await db.ActionLog
                .Where(l => l.BatchId == cursor.BatchId && pending.MessageIds.Contains(l.MessageId))
                .ToListAsync(t);
            foreach (var row in rows)
            {
                // The display names were written in the chunk's label order, so the same flags apply.
                row.LabelsAdded = Keep(row.LabelsAdded, keepAdd);
                row.LabelsRemoved = Keep(row.LabelsRemoved, keepRemove);
                row.LabelIdsAfter = LabelChunks.After(row.LabelIdsBefore, chunk.Add, chunk.StoredRemove);
                row.Note ??= addDropped ? LabelDeletedNote : null;
            }

            await db.SaveChangesAsync(t);
        }, ct);
        db.ChangeTracker.Clear();
        return next;

        static string[] Keep(string[] values, bool[] keep) =>
            values.Length == keep.Length ? [.. values.Where((_, i) => keep[i])] : values;
    }

    /// <summary>
    /// Sends the pending chunk, then stores its result and clears it. Ids Gmail answers 404 for are gone (noted on
    /// their inverse row); ids it refuses one by one are put back as not undone. A failure leaves the chunk pending
    /// unless this run sent it for the first time and Gmail certainly changed nothing: then the chunk is reverted.
    /// </summary>
    private async Task<(UndoCursor Cursor, JobSignal Signal, int Total)> SendAsync(
        JobContext ctx, UndoCursor cursor, bool resent, int total, CancellationToken ct)
    {
        var chunk = cursor.Pending!;
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!chunk.Gone && chunk.MessageIds.Length > 0 && chunk.Add.Length + chunk.Remove.Length > 0)
        {
            var sent = false;
            try
            {
                try
                {
                    await gmail.BatchModifyAsync(chunk.MessageIds, chunk.Add, chunk.Remove, ct);
                }
                catch (GoogleApiException ex) when (LabelChunks.IsBadIdOrLabel(ex))
                {
                    await LabelChunks.IsolateAsync(gmail, chunk.MessageIds, chunk.Add, chunk.Remove, ex, refused, () => sent = true, ct);
                    LabelChunks.ThrowIfCallRefused(chunk.MessageIds, refused, ex);
                }
            }
            catch (Exception ex) when (!resent && !sent && LabelChunks.NothingChanged(ex))
            {
                LogChunkReverted(logger, chunk.MessageIds.Length, ex);
                await RevertAsync(ctx, cursor, total);
                throw;
            }
        }

        var gone = chunk.Gone ? chunk.MessageIds : [.. refused.Where(r => r.Value == LabelChunks.NotFoundReason).Select(r => r.Key)];
        var rejected = refused.Where(r => r.Value != LabelChunks.NotFoundReason).Select(r => r.Key).ToArray();
        var reverted = chunk.Gone ? [] : chunk.MessageIds.Where(id => !refused.ContainsKey(id)).ToArray();
        var rejectedLogIds = await db.ActionLog.AsNoTracking()
            .Where(l => chunk.LogIds.Contains(l.Id) && rejected.Contains(l.MessageId))
            .Select(l => l.Id)
            .ToArrayAsync(ct);
        var done = cursor with
        {
            Pending = null,
            ChunksDone = cursor.ChunksDone + 1,
            MessagesDone = cursor.MessagesDone + reverted.Length,
            Gone = cursor.Gone + gone.Length,
            Skipped = rejectedLogIds.Length == 0 ? cursor.Skipped : [.. cursor.Skipped ?? [], .. rejectedLogIds],
        };
        total -= rejectedLogIds.Length;
        var signal = await ctx.CheckpointAsync(done, Progress(done, total), async t =>
        {
            var now = time.GetUtcNow();
            await PutBackAsync(cursor.BatchId, rejectedLogIds, t);
            await db.Messages.Where(m => gone.Contains(m.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, true).SetProperty(m => m.UpdatedAt, now), t);
            await db.ActionLog.Where(l => l.BatchId == cursor.BatchId && gone.Contains(l.MessageId))
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Note, GoneNote).SetProperty(l => l.LabelIdsAfter, l => l.LabelIdsBefore), t);
            await RestoreAsync(cursor, chunk, reverted, now, t);
        }, ct);
        db.ChangeTracker.Clear();
        return (done, signal, total);
    }

    /// <summary>The stored side of a reverted chunk: labels, suggestions back to approved, sender and batch counts.</summary>
    private async Task RestoreAsync(UndoCursor cursor, UndoChunk chunk, string[] reverted, DateTimeOffset now, CancellationToken ct)
    {
        if (reverted.Length == 0)
        {
            return;
        }

        var messages = await db.Messages.Where(m => reverted.Contains(m.Id)).ToDictionaryAsync(m => m.Id, StringComparer.Ordinal, ct);
        foreach (var message in messages.Values)
        {
            message.LabelIds = LabelChunks.After(message.LabelIds, chunk.Add, chunk.StoredRemove);
            message.UpdatedAt = now;
        }

        // A clean-up Delete undone: the messages are out of Trash, so stored and counted again.
        var restored = messages.Values.Where(m => m.DeletedInGmail && !m.LabelIds.Contains(Trash, StringComparer.Ordinal)).ToList();
        restored.ForEach(m => m.DeletedInGmail = false);

        var applied = await db.ActionLog
            .Where(l => chunk.LogIds.Contains(l.Id) && reverted.Contains(l.MessageId) && l.SuggestionId != null)
            .Select(l => new { l.MessageId, SuggestionId = l.SuggestionId!.Value })
            .ToListAsync(ct);
        var suggestionIds = applied.ConvertAll(l => l.SuggestionId);
        // Only rows of an applied suggestion counted towards applied_count (an auto-archive has none).
        var counted = applied.Select(l => l.MessageId).Distinct(StringComparer.Ordinal).ToArray();
        var suggestions = await db.Suggestions
            .Where(s => suggestionIds.Contains(s.Id) && s.Status == SuggestionStatus.Applied)
            .ToListAsync(ct);
        foreach (var suggestion in suggestions.Where(s => messages.ContainsKey(s.MessageId)))
        {
            // Keeps the approval time, as a reverted apply chunk does.
            suggestion.SetStatus(SuggestionStatus.Approved, messages[suggestion.MessageId], suggestion.DecidedAt ?? now);
        }

        await db.SaveChangesAsync(ct);
        await senders.UpdateAsync(restored.Select(m => m.FromAddress), ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE senders AS s SET applied_count = GREATEST(s.applied_count - c.n, 0)
            FROM (SELECT from_address, count(*)::int AS n FROM messages WHERE id = ANY({counted}) GROUP BY from_address) AS c
            WHERE s.address = c.from_address
            """, ct);
        await db.ActionBatches.Where(b => b.Id == cursor.BatchId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.MessageCount, b => b.MessageCount + reverted.Length), ct);
    }

    /// <summary>Undoes <see cref="PrepareAsync"/> for a chunk Gmail never saw.</summary>
    private async Task RevertAsync(JobContext ctx, UndoCursor cursor, int total)
    {
        db.ChangeTracker.Clear();
        var chunk = cursor.Pending!;
        await ctx.CheckpointAsync(
            cursor with { Pending = null }, Progress(cursor, total), t => PutBackAsync(cursor.BatchId, chunk.LogIds, t),
            CancellationToken.None);
        db.ChangeTracker.Clear();
    }

    /// <summary>Deletes the inverse rows of <paramref name="logIds"/> and marks those original rows not undone again.</summary>
    private async Task PutBackAsync(Guid undoBatchId, Guid[] logIds, CancellationToken ct)
    {
        if (logIds.Length == 0)
        {
            return;
        }

        var messageIds = await db.ActionLog.Where(l => logIds.Contains(l.Id)).Select(l => l.MessageId).ToArrayAsync(ct);
        await db.ActionLog.Where(l => l.BatchId == undoBatchId && messageIds.Contains(l.MessageId)).ExecuteDeleteAsync(ct);
        await db.ActionLog.Where(l => logIds.Contains(l.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.UndoneByBatchId, (Guid?)null), ct);
    }

    /// <summary>Deleted in Gmail and not merely in Trash, where an undo of a clean-up Delete finds it.</summary>
    private static bool Gone(MessageRow m) => m.DeletedInGmail && !m.LabelIds.Contains(Trash, StringComparer.Ordinal);

    private static int Done(UndoCursor cursor) => cursor.MessagesDone + cursor.Gone;

    private static JobProgress Progress(UndoCursor cursor, int total) => new(
        Done(cursor), total, $"Undid {cursor.MessagesDone} of {total} messages{(cursor.Gone > 0 ? $"; {cursor.Gone} skipped ({GoneNote})" : "")}");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail refused an undo chunk of {Count} messages before changing any; the chunk was reverted.")]
    private static partial void LogChunkReverted(ILogger logger, int count, Exception exception);

    private sealed class PlanChangedException : Exception;
}
