using GmailOrganiser.Data;
using GmailOrganiser.Jobs;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>Starts an apply: records the History batch and queues its <see cref="ApplyActionsJob"/>. No Gmail calls.</summary>
public sealed class ApplyService(AppDbContext db, IJobService jobs, TimeProvider time)
{
    /// <summary>
    /// The queued batch, or null when no matching suggestion is approved with a label Gmail accepts. Suggestions whose
    /// label Gmail would refuse are not counted; the job reports them as skipped.
    /// </summary>
    public async Task<ActionBatchDto?> StartAsync(string? senderAddress, Guid[]? suggestionIds, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var topics = await ApplyActionsJob.Eligible(db, now, senderAddress, suggestionIds).Select(s => s.TopicLabel).ToListAsync(ct);
        var count = topics.Count(LabelResolver.IsValid);
        if (count == 0)
        {
            return null;
        }

        var batch = new ActionBatchRow
        {
            Id = Guid.CreateVersion7(now),
            Kind = ActionKind.Apply,
            Description = ApplyActionsJob.Describe(count, senderAddress),
            CreatedAt = now,
        };

        // One transaction, so the job never runs without its batch and the batch always names its job.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ActionBatches.Add(batch);
        await db.SaveChangesAsync(ct);
        var cursor = new ApplyCursor(batch.Id, now, count, senderAddress, suggestionIds);
        var (job, _) = await jobs.EnqueueAsync(ApplyActionsJob.JobType, ApplyActionsJob.Queue, cursor, ct, batch.Id.ToString());
        batch.JobId = job.Id;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ActionBatchDto.From(batch);
    }
}
