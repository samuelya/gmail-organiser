using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using Google;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Senders;

/// <param name="CanonicalAddresses">The senders whose inbox mail is archived.</param>
/// <param name="DeleteLabelId">The delete label's Gmail id when the batch was created; its mail is left alone. Null when none.</param>
/// <param name="LastId">The highest message id (ordinal) of the last chunk stored; only later ids are planned.</param>
/// <param name="Gone">Messages Gmail no longer has; marked deleted, not logged.</param>
/// <param name="Pending">The chunk whose log is written and whose <c>batchModify</c> may have been sent.</param>
/// <param name="Skipped">Messages Gmail refused one by one; not logged and not retried by this job.</param>
public sealed record SenderArchiveCursor(
    Guid BatchId,
    string[] CanonicalAddresses,
    string? DeleteLabelId,
    string? LastId = null,
    int ChunksDone = 0,
    int MessagesDone = 0,
    int Gone = 0,
    string[]? Pending = null,
    string[]? Skipped = null);

/// <summary>
/// Archives the senders' inbox mail (#349, DESIGN §6.3): removes <c>INBOX</c> from every live message of the canonical
/// senders that is not delete-labelled and not protected, in id order. As <see cref="CleanUp.CleanUpActionsJob"/>, each
/// chunk is two transactions around the Gmail call: the first locks and re-checks the messages, writes the undo log and
/// checkpoints the chunk as pending; the second stores the labels, advances <see cref="SenderArchiveCursor.LastId"/> and
/// clears it. A pending chunk is resent on resume (removing <c>INBOX</c> again is a no-op); <see cref="UndoActionsJob"/>
/// reverts the batch from its log.
/// </summary>
public sealed partial class SenderArchiveJob(
    AppDbContext db,
    IGmailClient gmail,
    ISettingsStore settingsStore,
    SenderStatsUpdater senders,
    IOptions<GmailOptions> gmailOptions,
    TimeProvider time,
    ILogger<SenderArchiveJob> logger) : IJobHandler, IJobCancelHook
{
    public const string JobType = "sender_archive";
    public const string Queue = JobQueues.Apply;
    public const string InboxLabel = ActionPlanner.InboxLabel;
    public const string ProtectedReason = "protected";
    private static readonly string[] Remove = [InboxLabel];

    // Inbox messages of the senders left alone because they are protected; reported in every progress update.
    private int skippedProtected;

    public string Type => JobType;

    public static string Describe(int messages, int senders) =>
        $"Archive {messages} message{(messages == 1 ? "" : "s")} from {senders} sender{(senders == 1 ? "" : "s")}";

    /// <summary>Live inbox messages of the senders, not delete-labelled; protection is checked in memory.</summary>
    public static IQueryable<MessageRow> Candidates(AppDbContext db, string[] canonical, string? deleteLabelId)
    {
        var query = db.Messages.Where(m => canonical.Contains(m.CanonicalAddress) && !m.DeletedInGmail && m.LabelIds.Contains(InboxLabel));
        return deleteLabelId is null ? query : query.Where(m => !m.LabelIds.Contains(deleteLabelId));
    }

    /// <summary>The <see cref="Candidates"/> the job archives and the protected ones it skips, with the fields protection reads.</summary>
    public static async Task<(List<MessageRow> Covered, int Protected)> PlanAsync(
        AppDbContext db, string[] canonical, string? deleteLabelId, AppSettings settings, CancellationToken ct)
    {
        var rows = await Candidates(db, canonical, deleteLabelId)
            .Select(m => new MessageRow
            {
                Id = m.Id,
                FromAddress = m.FromAddress,
                LabelIds = m.LabelIds,
                HasAttachment = m.HasAttachment,
                ThreadReplied = m.ThreadReplied,
            })
            .AsNoTracking()
            .ToListAsync(ct);
        var allowlist = await AllowlistLoader.LoadAsync(db, settings, [.. rows.Select(m => m.FromAddress).Distinct()], ct);
        var covered = rows.Where(m => !MessageProtection.IsProtected(m, allowlist, settings.Protection))
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
        return (covered, rows.Count - covered.Count);
    }

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<SenderArchiveCursor>() ?? throw new JobRefusedException("The sender archive job has no batch.");
        if (!await db.ActionBatches.AnyAsync(b => b.Id == cursor.BatchId, ct))
        {
            throw new JobRefusedException("The action batch no longer exists.");
        }

        var plan = await ChunksAsync(cursor, ct);
        var total = Done(cursor) + (cursor.Pending?.Length ?? 0) + plan.Sum(c => c.Length);
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
                if (await PrepareAsync(ctx, cursor, chunk, total, ct) is not { } prepared)
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

            plan = await ChunksAsync(cursor, ct);
            total = Done(cursor) + plan.Sum(c => c.Length);
        }

        await ctx.CompleteAsync(cursor, Progress(cursor, total), _ => Task.CompletedTask, ct);
    }

    /// <summary>A chunk that may have reached Gmail must be finished by a resume, not cancelled.</summary>
    public bool AllowsCancel(string? cursor) =>
        cursor is null || JsonSerializer.Deserialize<SenderArchiveCursor>(cursor, JobRow.Json)?.Pending is null;

    /// <summary>Nothing to follow up: what was sent is logged and undoable from History.</summary>
    public Task CancelledAsync(string? cursor, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Chunks of the covered ids after <see cref="SenderArchiveCursor.LastId"/> and not pending or skipped.</summary>
    private async Task<List<string[]>> ChunksAsync(SenderArchiveCursor cursor, CancellationToken ct)
    {
        var (covered, protectedCount) = await PlanAsync(
            db, cursor.CanonicalAddresses, cursor.DeleteLabelId, await settingsStore.GetAsync(ct), ct);
        skippedProtected = protectedCount;
        var excluded = new HashSet<string>([.. cursor.Pending ?? [], .. cursor.Skipped ?? []], StringComparer.Ordinal);
        return [.. covered
            .Select(m => m.Id)
            .Where(id => (cursor.LastId is null || string.CompareOrdinal(id, cursor.LastId) > 0) && !excluded.Contains(id))
            .Chunk(gmailOptions.Value.BatchModifyMaxIds)];
    }

    /// <summary>
    /// Locks the chunk's messages, re-checks each (state can change between the click and the send), writes the undo
    /// log and checkpoints the chunk as pending, in one transaction. Null when anything changed since planning: nothing
    /// was written, the caller replans.
    /// </summary>
    private async Task<SenderArchiveCursor?> PrepareAsync(JobContext ctx, SenderArchiveCursor cursor, string[] ids, int total, CancellationToken ct)
    {
        var next = cursor with { Pending = ids };
        try
        {
            await ctx.CheckpointAsync(next, Progress(cursor, total), async t =>
            {
                var messages = await db.Messages
                    .FromSql($"SELECT * FROM messages WHERE id = ANY({ids}) ORDER BY id FOR UPDATE")
                    .ToListAsync(t);
                var settings = await settingsStore.GetAsync(t);
                var allowlist = await AllowlistLoader.LoadAsync(db, settings, [.. messages.Select(m => m.FromAddress).Distinct()], t);
                if (messages.Count != ids.Length || messages.Any(m => !Fits(m, settings, allowlist)))
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
                        LabelsAdded = [],
                        LabelsRemoved = Remove,
                        LabelIdsBefore = message.LabelIds,
                        LabelIdsAfter = LabelChunks.After(message.LabelIds, [], Remove),
                        CreatedAt = now,
                    });
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

        bool Fits(MessageRow m, AppSettings settings, Allowlist allowlist) =>
            !m.DeletedInGmail
            && cursor.CanonicalAddresses.Contains(m.CanonicalAddress, StringComparer.Ordinal)
            && m.LabelIds.Contains(InboxLabel, StringComparer.Ordinal)
            && (cursor.DeleteLabelId is null || !m.LabelIds.Contains(cursor.DeleteLabelId, StringComparer.Ordinal))
            && !MessageProtection.IsProtected(m, allowlist, settings.Protection);
    }

    /// <summary>
    /// Sends the pending chunk, then stores its result and clears it. Ids Gmail answers 404 for are marked deleted,
    /// ids it refuses one by one are skipped; neither keeps a log row. A failure leaves the chunk pending unless this
    /// run sent it for the first time and Gmail certainly changed nothing: then the chunk is reverted.
    /// </summary>
    private async Task<(SenderArchiveCursor Cursor, JobSignal Signal, int Total)> SendAsync(
        JobContext ctx, SenderArchiveCursor cursor, bool resent, int total, CancellationToken ct)
    {
        var ids = cursor.Pending!;
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);
        var sent = false;
        try
        {
            try
            {
                await gmail.BatchModifyAsync(ids, [], Remove, ct);
            }
            catch (GoogleApiException ex) when (LabelChunks.IsBadIdOrLabel(ex))
            {
                await LabelChunks.IsolateAsync(gmail, ids, [], Remove, ex, refused, () => sent = true, ct);
                LabelChunks.ThrowIfCallRefused(ids, refused, ex);
            }
        }
        catch (Exception ex) when (!resent && !sent && LabelChunks.NothingChanged(ex))
        {
            LogChunkReverted(logger, ids.Length, ex);
            await RevertAsync(ctx, cursor, total);
            throw;
        }

        string[] gone = [.. refused.Where(r => r.Value == LabelChunks.NotFoundReason).Select(r => r.Key)];
        string[] rejected = [.. refused.Where(r => r.Value != LabelChunks.NotFoundReason).Select(r => r.Key)];
        string[] changed = [.. ids.Where(id => !refused.ContainsKey(id))];
        var last = ids.Max(StringComparer.Ordinal);
        var done = cursor with
        {
            Pending = null,
            LastId = cursor.LastId is { } before && string.CompareOrdinal(before, last) > 0 ? before : last,
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
                message.LabelIds = LabelChunks.After(message.LabelIds, [], Remove);
                message.UpdatedAt = now;
            }

            await db.SaveChangesAsync(t);
            if (gone.Length > 0)
            {
                await senders.UpdateAsync(await db.Messages.Where(m => gone.Contains(m.Id)).Select(m => m.FromAddress).ToListAsync(t), t);
            }

            await db.ActionBatches.Where(b => b.Id == cursor.BatchId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.MessageCount, b => b.MessageCount + changed.Length), t);
        }, ct);
        db.ChangeTracker.Clear();
        return (done, signal, total);
    }

    /// <summary>Undoes <see cref="PrepareAsync"/> for a chunk Gmail never saw: its log rows are deleted.</summary>
    private async Task RevertAsync(JobContext ctx, SenderArchiveCursor cursor, int total)
    {
        db.ChangeTracker.Clear();
        var ids = cursor.Pending!;
        await ctx.CheckpointAsync(
            cursor with { Pending = null },
            Progress(cursor, total),
            t => db.ActionLog.Where(l => l.BatchId == cursor.BatchId && ids.Contains(l.MessageId)).ExecuteDeleteAsync(t),
            CancellationToken.None);
        db.ChangeTracker.Clear();
    }

    private static int Done(SenderArchiveCursor cursor) => cursor.MessagesDone + cursor.Gone;

    /// <summary>Messages archived of the total, and every skip with its reason (protected first).</summary>
    private JobProgress Progress(SenderArchiveCursor cursor, int total)
    {
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
            $"Archived {cursor.MessagesDone} of {total} messages{(reasons.Count > 0 ? $"; skipped {string.Join(", ", reasons)}" : "")}");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail refused a sender archive chunk of {Count} messages before changing any; the chunk was reverted.")]
    private static partial void LogChunkReverted(ILogger logger, int count, Exception exception);

    private sealed class PlanChangedException : Exception;
}
