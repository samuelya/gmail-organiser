using System.Net;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;
using Google;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Review;

/// <summary>
/// Auto-archive (DESIGN §6.3, setting <see cref="AppSettings.AutoArchiveOnActionDone"/>): a refreshed message whose
/// applied suggestion needs action, still in the inbox but without the action label, was marked done in Gmail, so it is
/// archived. Each chunk is an <see cref="ActionKind.AutoArchive"/> batch, undoable from History, written in two
/// transactions around the <c>batchModify</c>: the first writes the batch with <c>MessageCount = 0</c> (pending, not
/// undoable) and its log rows, the second stores the labels and the count. A pending batch is re-sent on the next scan;
/// after <see cref="MaxSendFailures"/> failed re-sends it is finalised as partial. Auto-archive never fails the fetch.
/// A message with any auto-archive log row, undone or not, is never archived again.
/// </summary>
public sealed partial class ActionDoneScanner(
    AppDbContext db,
    IGmailClient gmail,
    LabelCatalog catalog,
    ISettingsStore settingsStore,
    IOptions<GmailOptions> gmailOptions,
    TimeProvider time,
    ILogger<ActionDoneScanner> logger) : IActionDoneScanner
{
    public const string Description = "Auto-archive: action done";
    public const string PartialSuffix = " (partial)";
    public const int MaxSendFailures = 3;
    private static readonly string[] Remove = [ActionPlanner.InboxLabel];

    public async Task ScanAsync(IReadOnlyList<string> refreshedIds, CancellationToken ct)
    {
        await ResendPendingAsync(ct);
        if (refreshedIds.Count == 0)
        {
            return;
        }

        var settings = await settingsStore.GetAsync(ct);
        if (!settings.AutoArchiveOnActionDone)
        {
            return;
        }

        // The label list is only asked for when a message could qualify.
        var busy = await BusyBatchesAsync(ct);
        var candidates = await Due([.. refreshedIds], null, busy).Select(m => m.Id).ToArrayAsync(ct);
        if (candidates.Length == 0 || await catalog.FindByNameAsync(settings.ActionLabelName, ct) is not { } actionLabel)
        {
            return;
        }

        var due = await Due(candidates, actionLabel.Id, busy).OrderBy(m => m.Id).Select(m => m.Id).ToListAsync(ct);
        foreach (var (chunk, _, _) in LabelChunks.Group(due, _ => [], _ => Remove, gmailOptions.Value.BatchModifyMaxIds))
        {
            if (await PrepareAsync([.. chunk], actionLabel.Id, ct) is { } batchId)
            {
                await SendAsync(batchId, resent: false, ct);
            }
        }
    }

    /// <summary>
    /// Messages of <paramref name="ids"/> in the inbox with an applied suggestion that needs action and no auto-archive
    /// log row, none of whose log rows is in a <paramref name="busy"/> batch. With <paramref name="actionLabelId"/>, the
    /// suggestion's apply log row (not undone) must have added that label and the message must no longer carry it, so
    /// a renamed setting never archives older to-dos; without it the label is not checked.
    /// </summary>
    private IQueryable<MessageRow> Due(string[] ids, string? actionLabelId, Guid[] busy)
    {
        var query = db.Messages.Where(m => ids.Contains(m.Id) && !m.DeletedInGmail && m.LabelIds.Contains(ActionPlanner.InboxLabel)
            && db.Suggestions.Any(s => s.MessageId == m.Id && s.Status == SuggestionStatus.Applied && s.NeedsAction)
            && !db.ActionLog.Any(l => l.MessageId == m.Id
                && (busy.Contains(l.BatchId) || db.ActionBatches.Any(b => b.Id == l.BatchId && b.Kind == ActionKind.AutoArchive))));
        return actionLabelId is null
            ? query
            : query.Where(m => !m.LabelIds.Contains(actionLabelId)
                && db.Suggestions.Any(s => s.MessageId == m.Id && s.Status == SuggestionStatus.Applied && s.NeedsAction
                    && db.ActionLog.Any(l => l.SuggestionId == s.Id && l.MessageId == m.Id && l.UndoneByBatchId == null
                        && l.LabelIdsAfter.Contains(actionLabelId) && !l.LabelIdsBefore.Contains(actionLabelId)
                        && db.ActionBatches.Any(b => b.Id == l.BatchId && (b.Kind == ActionKind.Apply || b.Kind == ActionKind.ApplyRest)))));
    }

    /// <summary>
    /// Batches of apply and undo jobs that are queued, running, paused or failed with a pending chunk, and the batches
    /// those undo jobs revert: their messages may be between a log row and the <c>batchModify</c>, so they are skipped
    /// until a later fetch.
    /// </summary>
    private async Task<Guid[]> BusyBatchesAsync(CancellationToken ct)
    {
        string[] active = [.. JobRow.Active.Select(JobRow.FormatStatus)];
        var jobs = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM jobs
            WHERE type = ANY({ReviewJobTypes.WritesGmail})
              AND (status = ANY({active}) OR (status = {JobRow.FormatStatus(JobStatus.Failed)} AND cursor ->> 'pending' IS NOT NULL))
            """).ToListAsync(ct);
        if (jobs.Count == 0)
        {
            return [];
        }

        var batches = await db.ActionBatches.AsNoTracking()
            .Where(b => b.JobId != null && jobs.Contains(b.JobId.Value))
            .Select(b => new { b.Id, b.UndoOf })
            .ToListAsync(ct);
        return [.. batches.Select(b => b.Id).Concat(batches.Where(b => b.UndoOf != null).Select(b => b.UndoOf!.Value)).Distinct()];
    }

    /// <summary>Locks the messages, re-checks them and writes the pending batch and its undo log; null when none is still due.</summary>
    private async Task<Guid?> PrepareAsync(string[] ids, string actionLabelId, CancellationToken ct)
    {
        var busy = await BusyBatchesAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var messages = await db.Messages
            .FromSql($"SELECT * FROM messages WHERE id = ANY({ids}) ORDER BY id FOR UPDATE")
            .ToDictionaryAsync(m => m.Id, StringComparer.Ordinal, ct);
        var due = await Due(ids, actionLabelId, busy).Select(m => m.Id).ToListAsync(ct);
        if (due.Count == 0)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var batch = new ActionBatchRow { Id = Guid.CreateVersion7(now), Kind = ActionKind.AutoArchive, Description = Description, CreatedAt = now };
        db.ActionBatches.Add(batch);
        foreach (var id in due)
        {
            var before = messages[id].LabelIds;
            db.ActionLog.Add(new ActionLogRow
            {
                Id = Guid.CreateVersion7(now),
                BatchId = batch.Id,
                MessageId = id,
                LabelsRemoved = Remove,
                LabelIdsBefore = before,
                LabelIdsAfter = LabelChunks.After(before, [], Remove),
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return batch.Id;
    }

    /// <summary>Re-sends batches a crash or a failed send left pending; removing <c>INBOX</c> again changes nothing.</summary>
    private async Task ResendPendingAsync(CancellationToken ct)
    {
        var pending = await db.ActionBatches.AsNoTracking()
            .Where(b => b.Kind == ActionKind.AutoArchive && b.MessageCount == 0)
            .OrderBy(b => b.Id)
            .Select(b => b.Id)
            .ToListAsync(ct);
        foreach (var batchId in pending)
        {
            await SendAsync(batchId, resent: true, ct);
        }
    }

    /// <summary>
    /// Sends the batch's <c>batchModify</c>, isolating ids Gmail refuses on their own (skipped, their log rows removed),
    /// then stores the result. A first send Gmail certainly refused deletes the batch, so a later fetch tries again. Any
    /// other failure is only logged and leaves the batch pending; a failed re-send is counted, and the
    /// <see cref="MaxSendFailures"/>th finalises the batch as partial, keeping its undo log. Only cancellation is thrown.
    /// </summary>
    /// <param name="resent">The batch was pending, so an earlier send may have reached Gmail.</param>
    private async Task SendAsync(Guid batchId, bool resent, CancellationToken ct)
    {
        var ids = await db.ActionLog.AsNoTracking().Where(l => l.BatchId == batchId).OrderBy(l => l.MessageId).Select(l => l.MessageId).ToArrayAsync(ct);
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);
        var sent = false;
        try
        {
            if (ids.Length > 0)
            {
                await ModifyAsync(ids, refused, () => sent = true, ct);
            }
        }
        catch (Exception ex) when (!resent && !sent && LabelChunks.NothingChanged(ex) && !ct.IsCancellationRequested)
        {
            LogReverted(logger, ids.Length, ex.GetType().Name);
            await db.ActionLog.Where(l => l.BatchId == batchId).ExecuteDeleteAsync(CancellationToken.None);
            await db.ActionBatches.Where(b => b.Id == batchId).ExecuteDeleteAsync(CancellationToken.None);
            return;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await SendFailedAsync(batchId, resent, ids.Length, ex);
            return;
        }

        await StoreAsync(batchId, ids, refused, ct);
    }

    /// <summary>Leaves the batch pending; counts a failed re-send and finalises the batch as partial at the limit.</summary>
    private async Task SendFailedAsync(Guid batchId, bool resent, int count, Exception ex)
    {
        var status = (ex as GoogleApiException)?.HttpStatusCode;
        if (!resent)
        {
            LogSendFailed(logger, count, ex.GetType().Name, status);
            return;
        }

        await db.ActionBatches.Where(b => b.Id == batchId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.SendFailures, b => b.SendFailures + 1), CancellationToken.None);
        var failures = await db.ActionBatches.Where(b => b.Id == batchId).Select(b => b.SendFailures).SingleAsync(CancellationToken.None);
        LogResendFailed(logger, count, failures, ex.GetType().Name, status);
        if (failures < MaxSendFailures)
        {
            return;
        }

        // Gmail may or may not have archived them: the log stays so History can undo, the stored labels stay as read.
        await db.ActionBatches.Where(b => b.Id == batchId).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.MessageCount, count)
            .SetProperty(b => b.Description, b => b.Description + PartialSuffix), CancellationToken.None);
        LogFinalisedPartial(logger, count);
    }

    private async Task ModifyAsync(string[] ids, Dictionary<string, string> refused, Action sent, CancellationToken ct)
    {
        try
        {
            await gmail.BatchModifyAsync(ids, [], Remove, ct);
            sent();
        }
        catch (GoogleApiException ex) when (LabelChunks.IsBadIdOrLabel(ex))
        {
            await LabelChunks.IsolateAsync(gmail, ids, [], Remove, ex, refused, sent, ct);
            LabelChunks.ThrowIfCallRefused(ids, refused, ex);
        }
    }

    /// <summary>Removes the refused ids' log rows, stores the archived labels and sets the count (deleting an empty batch).</summary>
    private async Task StoreAsync(Guid batchId, string[] ids, Dictionary<string, string> refused, CancellationToken ct)
    {
        var archived = ids.Where(id => !refused.ContainsKey(id)).ToArray();
        var gone = refused.Where(r => r.Value == LabelChunks.NotFoundReason).Select(r => r.Key).ToArray();
        var skipped = refused.Keys.ToArray();
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ActionLog.Where(l => l.BatchId == batchId && skipped.Contains(l.MessageId)).ExecuteDeleteAsync(ct);
        await db.Messages.Where(m => gone.Contains(m.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, true).SetProperty(m => m.UpdatedAt, now), ct);
        foreach (var message in await db.Messages.Where(m => archived.Contains(m.Id)).ToListAsync(ct))
        {
            message.LabelIds = LabelChunks.After(message.LabelIds, [], Remove);
            message.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        if (archived.Length == 0)
        {
            await db.ActionBatches.Where(b => b.Id == batchId).ExecuteDeleteAsync(ct);
        }
        else
        {
            await db.ActionBatches.Where(b => b.Id == batchId).ExecuteUpdateAsync(s => s.SetProperty(b => b.MessageCount, archived.Length), ct);
            LogArchived(logger, archived.Length);
        }

        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Auto-archived {Count} messages whose action label was removed.")]
    private static partial void LogArchived(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail refused an auto-archive of {Count} messages before changing any ({Error}); the batch was dropped.")]
    private static partial void LogReverted(ILogger logger, int count, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auto-archive of {Count} messages failed ({Error}, HTTP {Status}); the batch stays pending.")]
    private static partial void LogSendFailed(ILogger logger, int count, string error, HttpStatusCode? status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-sending an auto-archive of {Count} messages failed ({Failures}) ({Error}, HTTP {Status}).")]
    private static partial void LogResendFailed(ILogger logger, int count, int failures, string error, HttpStatusCode? status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An auto-archive of {Count} messages was finalised as partial after repeated failed re-sends.")]
    private static partial void LogFinalisedPartial(ILogger logger, int count);
}
