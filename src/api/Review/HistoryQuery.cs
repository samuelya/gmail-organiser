using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>The History page: action batches newest first, one batch's log, and starting an undo. No Gmail writes.</summary>
public sealed class HistoryQuery(AppDbContext db, IJobService jobs, LabelCatalog catalog, TimeProvider time)
{
    public const int MaxRows = 500;

    public async Task<PagedDto<ActionBatchDto>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        var total = await db.ActionBatches.LongCountAsync(ct);
        var rows = await db.ActionBatches.AsNoTracking()
            .OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        return new PagedDto<ActionBatchDto>(rows.ConvertAll(ActionBatchDto.From), page, pageSize, total);
    }

    /// <summary>The batch with its first <see cref="MaxRows"/> log rows, or null when it does not exist.</summary>
    public async Task<ActionBatchDetailDto?> GetAsync(Guid id, CancellationToken ct)
    {
        if (await db.ActionBatches.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct) is not { } batch)
        {
            return null;
        }

        var rows = await db.ActionLog.AsNoTracking()
            .Where(l => l.BatchId == id)
            .OrderBy(l => l.Id)
            .Take(MaxRows + 1)
            .Select(l => new { Row = l, Subject = db.Messages.Where(m => m.Id == l.MessageId).Select(m => m.Subject).FirstOrDefault() })
            .ToListAsync(ct);
        var names = await LabelNamesAsync(ct);
        var dtos = rows.Take(MaxRows).Select(r =>
        {
            string[] added = [.. r.Row.LabelIdsAfter.Except(r.Row.LabelIdsBefore, StringComparer.Ordinal)];
            string[] removed = [.. r.Row.LabelIdsBefore.Except(r.Row.LabelIdsAfter, StringComparer.Ordinal)];
            return new ActionLogRowDto(
                r.Row.Id,
                r.Row.MessageId,
                r.Subject,
                added,
                removed,
                [.. added.Select(l => names.GetValueOrDefault(l, l))],
                [.. removed.Select(l => names.GetValueOrDefault(l, l))],
                r.Row.Note,
                r.Row.UndoneByBatchId);
        }).ToList();
        return new ActionBatchDetailDto(ActionBatchDto.From(batch), dtos, rows.Count > MaxRows);
    }

    /// <summary>
    /// Records an undo batch for <paramref name="id"/> and queues its <see cref="UndoActionsJob"/>, in one transaction
    /// that holds the original batch's row, so two requests never both start one.
    /// </summary>
    public async Task<UndoStart> StartUndoAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var original = await db.ActionBatches.FromSql($"SELECT * FROM action_batches WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (original is null)
        {
            return UndoStart.NotFound;
        }

        if (!ActionBatchDto.From(original).CanUndo)
        {
            return UndoStart.Refused("This batch cannot be undone: it is an undo, already undone or changed nothing.");
        }

        // A failed or paused apply could still add rows after the undo; only a finished one cannot.
        if (original.JobId is { } applyJob
            && await db.Jobs.AnyAsync(j => j.Id == applyJob && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled, ct))
        {
            return UndoStart.Refused("The batch is still being applied. Let it finish, or cancel it, before undoing it.");
        }

        // An active undo, or a failed one whose pending chunk only a resume can finish.
        string[] active = [.. JobRow.Active.Select(JobRow.FormatStatus)];
        var failed = JobRow.FormatStatus(JobStatus.Failed);
        if (await db.Database.SqlQuery<int>($"""
                SELECT 1 AS "Value" FROM jobs AS j JOIN action_batches AS b ON b.job_id = j.id
                WHERE b.undo_of = {id} AND (j.status = ANY({active}) OR (j.status = {failed} AND j.cursor ->> 'pending' IS NOT NULL))
                """).AnyAsync(ct))
        {
            return UndoStart.Refused("An undo of this batch is already queued or must be resumed.");
        }

        var now = time.GetUtcNow();
        var batch = new ActionBatchRow
        {
            Id = Guid.CreateVersion7(now),
            Kind = ActionKind.Undo,
            Description = UndoActionsJob.Describe(original.Description),
            UndoOf = original.Id,
            CreatedAt = now,
        };
        db.ActionBatches.Add(batch);
        await db.SaveChangesAsync(ct);
        var (job, _) = await jobs.EnqueueAsync(
            UndoActionsJob.JobType, UndoActionsJob.Queue, new UndoCursor(batch.Id, original.Id), ct, batch.Id.ToString());
        batch.JobId = job.Id;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new UndoStart(ActionBatchDto.From(batch), null);
    }

    /// <summary>Label id to name; empty while Gmail can't be asked, so ids are shown instead.</summary>
    private async Task<Dictionary<string, string>> LabelNamesAsync(CancellationToken ct)
    {
        try
        {
            return (await catalog.GetAsync(ct)).ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is GmailNotConnectedException or GmailRateLimitedException)
        {
            return [];
        }
    }
}

/// <summary>The undo batch, or why none was started; both null when the batch does not exist.</summary>
public sealed record UndoStart(ActionBatchDto? Batch, string? Refusal)
{
    public static readonly UndoStart NotFound = new(null, null);

    public static UndoStart Refused(string reason) => new(null, reason);
}
