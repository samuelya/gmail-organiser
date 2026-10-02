using System.Net;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
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
/// undoable) and its log rows, the second stores the labels and the count. A pending batch is re-sent on the next scan.
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
        var candidates = await Due([.. refreshedIds], null).Select(m => m.Id).ToArrayAsync(ct);
        if (candidates.Length == 0 || await catalog.FindByNameAsync(settings.ActionLabelName, ct) is not { } actionLabel)
        {
            return;
        }

        var due = await Due(candidates, actionLabel.Id).OrderBy(m => m.Id).Select(m => m.Id).ToListAsync(ct);
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
    /// log row; without <paramref name="actionLabelId"/> the action label is not checked.
    /// </summary>
    private IQueryable<MessageRow> Due(string[] ids, string? actionLabelId)
    {
        var query = db.Messages.Where(m => ids.Contains(m.Id) && !m.DeletedInGmail && m.LabelIds.Contains(ActionPlanner.InboxLabel)
            && db.Suggestions.Any(s => s.MessageId == m.Id && s.Status == SuggestionStatus.Applied && s.NeedsAction)
            && !db.ActionLog.Any(l => l.MessageId == m.Id
                && db.ActionBatches.Any(b => b.Id == l.BatchId && b.Kind == ActionKind.AutoArchive)));
        return actionLabelId is null ? query : query.Where(m => !m.LabelIds.Contains(actionLabelId));
    }

    /// <summary>Locks the messages, re-checks them and writes the pending batch and its undo log; null when none is still due.</summary>
    private async Task<Guid?> PrepareAsync(string[] ids, string actionLabelId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var messages = await db.Messages
            .FromSql($"SELECT * FROM messages WHERE id = ANY({ids}) ORDER BY id FOR UPDATE")
            .ToDictionaryAsync(m => m.Id, StringComparer.Ordinal, ct);
        var due = await Due(ids, actionLabelId).Select(m => m.Id).ToListAsync(ct);
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
    /// then stores the result. A first send Gmail certainly refused deletes the batch, so a later fetch tries again; a
    /// whole-call 4xx is only logged, so it never stops the fetch. Any other failure leaves the batch pending.
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
        catch (Exception ex) when (!resent && !sent && LabelChunks.NothingChanged(ex))
        {
            LogReverted(logger, ids.Length, ex);
            await db.ActionLog.Where(l => l.BatchId == batchId).ExecuteDeleteAsync(CancellationToken.None);
            await db.ActionBatches.Where(b => b.Id == batchId).ExecuteDeleteAsync(CancellationToken.None);
            if (ex is not GoogleApiException { HttpStatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError })
            {
                throw;
            }

            return;
        }

        await StoreAsync(batchId, ids, refused, ct);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail refused an auto-archive of {Count} messages before changing any; the batch was dropped.")]
    private static partial void LogReverted(ILogger logger, int count, Exception exception);
}
