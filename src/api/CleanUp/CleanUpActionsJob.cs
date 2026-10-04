using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Google;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.CleanUp;

public static class CleanUpJobTypes
{
    /// <summary>Trashes or unmarks delete-labelled messages (<see cref="CleanUpActionsJob"/>); its dedup key is the batch id.</summary>
    public const string Actions = "cleanup_actions";
}

/// <param name="Kind"><see cref="ActionKind.Trash"/> or <see cref="ActionKind.Unmark"/>.</param>
/// <param name="DeleteLabelId">The delete label's Gmail id when the batch was created.</param>
/// <param name="Gone">Messages Gmail no longer has; marked deleted, not logged.</param>
/// <param name="Pending">The chunk whose log is written and whose <c>batchModify</c> may have been sent.</param>
/// <param name="Skipped">Messages Gmail refused one by one; not logged and not retried by this job.</param>
public sealed record CleanUpCursor(
    Guid BatchId,
    ActionKind Kind,
    string DeleteLabelId,
    CleanUpSelection Selection,
    bool IncludeProtected,
    int ChunksDone = 0,
    int MessagesDone = 0,
    int Gone = 0,
    CleanUpChunk? Pending = null,
    string[]? Skipped = null);

/// <summary>One <c>batchModify</c> call: the same label ids added and removed on every message.</summary>
public sealed record CleanUpChunk(string[] MessageIds, string[] Add, string[] Remove);

/// <summary>
/// Moves the selected delete-labelled messages to Trash (adds <c>TRASH</c>, removes <c>INBOX</c>; the delete label
/// stays) or removes the delete label from them (DESIGN §3.3, §3.6). Like <see cref="UndoActionsJob"/>, each chunk is
/// two transactions around the Gmail call: the first locks the messages, re-checks them (still labelled, not in Trash,
/// unprotected unless the batch includes protected mail), writes the undo log and checkpoints the chunk as pending;
/// the second stores the labels, <c>deleted_in_gmail</c> and sender stats and clears it. A pending chunk is resent on
/// resume, which Gmail treats as a no-op for what it already applied.
/// </summary>
public sealed partial class CleanUpActionsJob(
    AppDbContext db,
    IGmailClient gmail,
    LabelCatalog catalog,
    ISettingsStore settingsStore,
    SenderStatsUpdater senders,
    IOptions<GmailOptions> gmailOptions,
    TimeProvider time,
    ILogger<CleanUpActionsJob> logger) : IJobHandler, IJobCancelHook
{
    public const string JobType = CleanUpJobTypes.Actions;
    public const string Queue = JobQueues.Apply;
    public const string ProtectedReason = "protected";

    // Selected messages the current plan leaves alone because they are protected; reported in every progress update.
    private int skippedProtected;

    public string Type => JobType;

    public static string Describe(ActionKind kind, int count, string? senderAddress) =>
        $"{(kind == ActionKind.Trash ? "Delete" : "Removed from clean-up")}: {count} message{(count == 1 ? "" : "s")}"
        + (senderAddress is null ? "" : $" from {senderAddress}");

    /// <summary>Whether the batch changes <paramref name="m"/>: Delete skips protected mail unless told otherwise.</summary>
    public static bool Covers(ActionKind kind, bool includeProtected, MessageRow m, Allowlist allowlist, ProtectionSettings rules) =>
        kind == ActionKind.Unmark || includeProtected || !MessageProtection.IsProtected(m, allowlist, rules);

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<CleanUpCursor>() ?? throw new JobRefusedException("The clean-up job has no batch.");
        if (!await db.ActionBatches.AnyAsync(b => b.Id == cursor.BatchId, ct))
        {
            throw new JobRefusedException("The action batch no longer exists.");
        }

        var names = (await catalog.GetAsync(ct)).ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
        var settings = await settingsStore.GetAsync(ct);
        // The pending chunk's rows aren't stored as changed yet, so the plan would cover them a second time.
        var plan = Without(await PlanAsync(cursor, settings, ct), cursor.Pending);
        var total = Done(cursor) + (cursor.Pending?.MessageIds.Length ?? 0) + plan.Sum(c => c.MessageIds.Length);
        if (cursor.Pending is not null)
        {
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
                if (await PrepareAsync(ctx, cursor, chunk, settings, names, total, ct) is not { } prepared)
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

            plan = await PlanAsync(cursor, settings, ct);
            total = Done(cursor) + plan.Sum(c => c.MessageIds.Length);
        }

        await ctx.CompleteAsync(cursor, Progress(cursor, total), _ => Task.CompletedTask, ct);
    }

    /// <summary>A chunk that may have reached Gmail must be finished by a resume, not cancelled.</summary>
    public bool AllowsCancel(string? cursor) =>
        cursor is null || JsonSerializer.Deserialize<CleanUpCursor>(cursor, JobRow.Json)?.Pending is null;

    /// <summary>Nothing to follow up: what was sent is logged and undoable from History.</summary>
    public Task CancelledAsync(string? cursor, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The label ids the batch adds to and removes from <paramref name="m"/>.</summary>
    public static (string[] Add, string[] Remove) Change(ActionKind kind, string deleteLabelId, MessageRow m) =>
        kind == ActionKind.Trash
            ? ([CleanUpQuery.TrashLabel], m.LabelIds.Contains(CleanUpQuery.InboxLabel, StringComparer.Ordinal) ? [CleanUpQuery.InboxLabel] : [])
            : ([], [deleteLabelId]);

    /// <summary>Chunks of the selected messages still to change, in id order; protected ones are counted, not planned.</summary>
    private async Task<List<CleanUpChunk>> PlanAsync(CleanUpCursor cursor, AppSettings settings, CancellationToken ct)
    {
        var skipped = cursor.Skipped ?? [];
        var rows = await CleanUpQuery.Selected(db, cursor.DeleteLabelId, cursor.Selection)
            .Where(m => !skipped.Contains(m.Id))
            .OrderBy(m => m.Id)
            .Select(m => new MessageRow
            {
                Id = m.Id, FromAddress = m.FromAddress, LabelIds = m.LabelIds, HasAttachment = m.HasAttachment, ThreadReplied = m.ThreadReplied,
            })
            .AsNoTracking()
            .ToListAsync(ct);
        var allowlist = await AllowlistLoader.LoadAsync(db, settings, ct);
        var covered = rows.Where(m => Covers(cursor.Kind, cursor.IncludeProtected, m, allowlist, settings.Protection)).ToList();
        skippedProtected = rows.Count - covered.Count;
        var changes = covered.Select(m => (m.Id, Change: Change(cursor.Kind, cursor.DeleteLabelId, m)));
        return [.. LabelChunks.Group(changes, c => c.Change.Add, c => c.Change.Remove, gmailOptions.Value.BatchModifyMaxIds)
            .Select(c => new CleanUpChunk([.. c.Items.Select(i => i.Id)], c.Add, c.Remove))];
    }

    /// <summary>
    /// Locks the chunk's messages, re-checks each against the batch (state can change between the click and the
    /// send), writes the undo log and checkpoints the chunk as pending, in one transaction. Null when anything changed
    /// since planning: nothing was written, the caller replans.
    /// </summary>
    private async Task<CleanUpCursor?> PrepareAsync(
        JobContext ctx,
        CleanUpCursor cursor,
        CleanUpChunk chunk,
        AppSettings settings,
        IReadOnlyDictionary<string, string> names,
        int total,
        CancellationToken ct)
    {
        var next = cursor with { Pending = chunk };
        try
        {
            await ctx.CheckpointAsync(next, Progress(cursor, total), async t =>
            {
                var ids = chunk.MessageIds;
                var messages = await db.Messages
                    .FromSql($"SELECT * FROM messages WHERE id = ANY({ids}) ORDER BY id FOR UPDATE")
                    .ToListAsync(t);
                var allowlist = await AllowlistLoader.LoadAsync(db, settings, t);
                if (messages.Count != ids.Length || messages.Any(m => !Fits(m)))
                {
                    throw new PlanChangedException();
                }

                var now = time.GetUtcNow();
                foreach (var message in messages)
                {
                    db.ActionLog.Add(new ActionLogRow
                    {
                        Id = Guid.CreateVersion7(now),
                        BatchId = cursor.BatchId,
                        MessageId = message.Id,
                        LabelsAdded = [.. chunk.Add.Select(id => names.GetValueOrDefault(id, id))],
                        LabelsRemoved = [.. chunk.Remove.Select(id => names.GetValueOrDefault(id, id))],
                        LabelIdsBefore = message.LabelIds,
                        LabelIdsAfter = LabelChunks.After(message.LabelIds, chunk.Add, chunk.Remove),
                        CreatedAt = now,
                    });
                }

                await db.SaveChangesAsync(t);

                bool Fits(MessageRow m)
                {
                    var (add, remove) = Change(cursor.Kind, cursor.DeleteLabelId, m);
                    return !m.DeletedInGmail
                        && m.LabelIds.Contains(cursor.DeleteLabelId, StringComparer.Ordinal)
                        && !m.LabelIds.Contains(CleanUpQuery.TrashLabel, StringComparer.Ordinal)
                        && Covers(cursor.Kind, cursor.IncludeProtected, m, allowlist, settings.Protection)
                        && LabelChunks.Sorted(add).SequenceEqual(chunk.Add)
                        && LabelChunks.Sorted(remove).SequenceEqual(chunk.Remove);
                }
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
    /// Sends the pending chunk, then stores its result and clears it. Ids Gmail answers 404 for are marked deleted,
    /// ids it refuses one by one are skipped; neither keeps a log row. A failure leaves the chunk pending unless this
    /// run sent it for the first time and Gmail certainly changed nothing: then the chunk is reverted.
    /// </summary>
    private async Task<(CleanUpCursor Cursor, JobSignal Signal, int Total)> SendAsync(
        JobContext ctx, CleanUpCursor cursor, bool resent, int total, CancellationToken ct)
    {
        var chunk = cursor.Pending!;
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);
        if (chunk.MessageIds.Length > 0 && chunk.Add.Length + chunk.Remove.Length > 0)
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

        string[] gone = [.. refused.Where(r => r.Value == LabelChunks.NotFoundReason).Select(r => r.Key)];
        string[] rejected = [.. refused.Where(r => r.Value != LabelChunks.NotFoundReason).Select(r => r.Key)];
        string[] changed = [.. chunk.MessageIds.Where(id => !refused.ContainsKey(id))];
        var done = cursor with
        {
            Pending = null,
            ChunksDone = cursor.ChunksDone + 1,
            MessagesDone = cursor.MessagesDone + changed.Length,
            Gone = cursor.Gone + gone.Length,
            Skipped = rejected.Length == 0 ? cursor.Skipped : [.. cursor.Skipped ?? [], .. rejected],
        };
        total -= rejected.Length;
        var signal = await ctx.CheckpointAsync(done, Progress(done, total), async t =>
        {
            var now = time.GetUtcNow();
            string[] unlogged = [.. refused.Keys];
            await db.ActionLog.Where(l => l.BatchId == cursor.BatchId && unlogged.Contains(l.MessageId)).ExecuteDeleteAsync(t);
            await db.Messages.Where(m => gone.Contains(m.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, true).SetProperty(m => m.UpdatedAt, now), t);

            var messages = await db.Messages.Where(m => changed.Contains(m.Id)).ToListAsync(t);
            foreach (var message in messages)
            {
                message.LabelIds = LabelChunks.After(message.LabelIds, chunk.Add, chunk.Remove);
                message.DeletedInGmail = message.LabelIds.Contains(CleanUpQuery.TrashLabel, StringComparer.Ordinal);
                message.UpdatedAt = now;
            }

            await db.SaveChangesAsync(t);
            if (cursor.Kind == ActionKind.Trash || gone.Length > 0)
            {
                var addresses = await db.Messages.Where(m => changed.Contains(m.Id) || gone.Contains(m.Id))
                    .Select(m => m.FromAddress).ToListAsync(t);
                await senders.UpdateAsync(addresses, t);
            }

            await db.ActionBatches.Where(b => b.Id == cursor.BatchId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.MessageCount, b => b.MessageCount + changed.Length), t);
        }, ct);
        db.ChangeTracker.Clear();
        return (done, signal, total);
    }

    /// <summary>Undoes <see cref="PrepareAsync"/> for a chunk Gmail never saw: its log rows are deleted.</summary>
    private async Task RevertAsync(JobContext ctx, CleanUpCursor cursor, int total)
    {
        db.ChangeTracker.Clear();
        var ids = cursor.Pending!.MessageIds;
        await ctx.CheckpointAsync(
            cursor with { Pending = null },
            Progress(cursor, total),
            t => db.ActionLog.Where(l => l.BatchId == cursor.BatchId && ids.Contains(l.MessageId)).ExecuteDeleteAsync(t),
            CancellationToken.None);
        db.ChangeTracker.Clear();
    }

    private static List<CleanUpChunk> Without(List<CleanUpChunk> plan, CleanUpChunk? pending)
    {
        if (pending is null)
        {
            return plan;
        }

        var sent = pending.MessageIds.ToHashSet(StringComparer.Ordinal);
        return [.. plan.Select(c => c with { MessageIds = [.. c.MessageIds.Where(id => !sent.Contains(id))] })
            .Where(c => c.MessageIds.Length > 0)];
    }

    private static int Done(CleanUpCursor cursor) => cursor.MessagesDone + cursor.Gone;

    /// <summary>Messages changed of the total, and every skip with its reason (protected first).</summary>
    private JobProgress Progress(CleanUpCursor cursor, int total)
    {
        var verb = cursor.Kind == ActionKind.Trash ? "Moved to Trash" : "Removed from clean-up";
        var reasons = new List<string>();
        if (skippedProtected > 0)
        {
            reasons.Add($"{skippedProtected} {ProtectedReason}");
        }

        if (cursor.Gone > 0)
        {
            reasons.Add($"{cursor.Gone} {LabelChunks.NotFoundReason}");
        }

        if (cursor.Skipped is { Length: > 0 } skipped)
        {
            reasons.Add($"{skipped.Length} {LabelChunks.RefusedReason}");
        }

        return new(Done(cursor), total,
            $"{verb} {cursor.MessagesDone} of {total} messages{(reasons.Count > 0 ? $"; skipped {string.Join(", ", reasons)}" : "")}");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail refused a clean-up chunk of {Count} messages before changing any; the chunk was reverted.")]
    private static partial void LogChunkReverted(ILogger logger, int count, Exception exception);

    private sealed class PlanChangedException : Exception;
}
