using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.CleanUp;

/// <summary>Starts a clean-up action: records the History batch and queues its <see cref="CleanUpActionsJob"/>. No Gmail calls.</summary>
public sealed class CleanUpService(AppDbContext db, CleanUpQuery query, ISettingsStore settingsStore, IJobService jobs, TimeProvider time)
{
    /// <summary>
    /// The queued batch with how many messages it covers and how many protected ones Delete skips; null when no
    /// selected message qualifies (or the delete label does not exist in Gmail).
    /// </summary>
    /// <param name="kind"><see cref="ActionKind.Trash"/> or <see cref="ActionKind.Unmark"/>.</param>
    /// <exception cref="Gmail.GmailNotConnectedException">The app is not connected to Gmail.</exception>
    public async Task<CleanupBatchDto?> StartAsync(
        ActionKind kind, CleanUpSelection selection, bool includeProtected, CancellationToken ct)
    {
        if (await query.DeleteLabelIdAsync(ct) is not { } label)
        {
            return null;
        }

        var settings = await settingsStore.GetAsync(ct);
        var rows = await CleanUpQuery.Selected(db, label, selection)
            .Select(m => new MessageRow
            {
                Id = m.Id, FromAddress = m.FromAddress, LabelIds = m.LabelIds, HasAttachment = m.HasAttachment, ThreadReplied = m.ThreadReplied,
            })
            .AsNoTracking()
            .ToListAsync(ct);
        var allowlist = await AllowlistLoader.LoadAsync(db, settings, ct);
        var queued = rows.Count(m => CleanUpActionsJob.Covers(kind, includeProtected, m, allowlist, settings.Protection));
        if (queued == 0)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var batch = new ActionBatchRow
        {
            Id = Guid.CreateVersion7(now),
            Kind = kind,
            Description = CleanUpActionsJob.Describe(kind, queued, selection.SenderAddress),
            CreatedAt = now,
        };

        // One transaction, so the job never runs without its batch and the batch always names its job.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ActionBatches.Add(batch);
        await db.SaveChangesAsync(ct);
        var cursor = new CleanUpCursor(batch.Id, kind, label, selection, includeProtected);
        var (job, _) = await jobs.EnqueueAsync(CleanUpActionsJob.JobType, CleanUpActionsJob.Queue, cursor, ct, batch.Id.ToString());
        batch.JobId = job.Id;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CleanupBatchDto(ActionBatchDto.From(batch), queued, rows.Count - queued);
    }
}
