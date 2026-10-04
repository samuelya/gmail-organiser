using System.Net;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using Google;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

public sealed partial class UndoActionsJob
{
    /// <summary>
    /// For a label merge batch, finds or re-creates by name each source label deleted since the merge (the label plan
    /// deletes an emptied source), so the undo never takes the target off mail without giving the source back. Returns
    /// the labels after, and the stored source ids mapped to the label now carrying the name; a re-created label is
    /// recorded on the undo batch. A re-run finds the label by name. When a name can't be created the run is refused
    /// before anything is sent, so this run removes no label.
    /// </summary>
    private async Task<(IReadOnlyList<GmailLabel> Labels, Dictionary<string, string> Moved)> RestoreMergeSourcesAsync(
        UndoCursor cursor, IReadOnlyList<GmailLabel> labels, CancellationToken ct)
    {
        var moved = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!await db.ActionBatches.AnyAsync(b => b.Id == cursor.UndoOf && b.Kind == ActionKind.LabelMerge, ct))
        {
            return (labels, moved);
        }

        var existing = labels.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var rows = await db.ActionLog.AsNoTracking()
            .Where(l => l.BatchId == cursor.UndoOf && (l.UndoneByBatchId == null || l.UndoneByBatchId == cursor.BatchId))
            .Select(l => new { l.LabelIdsBefore, l.LabelIdsAfter, l.LabelsRemoved })
            .Distinct()
            .ToListAsync(ct);
        // A merge row removes exactly the source; a row Gmail refused removed nothing.
        var sources = rows
            .Select(r => (Ids: r.LabelIdsBefore.Except(r.LabelIdsAfter, StringComparer.Ordinal).ToArray(), Names: r.LabelsRemoved))
            .Where(r => r.Ids.Length == 1 && r.Names.Length == 1 && !existing.Contains(r.Ids[0]))
            .Select(r => (Id: r.Ids[0], Name: r.Names[0]))
            .DistinctBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
        var created = false;
        foreach (var (id, name) in sources)
        {
            var label = GmailLabel.FindByName(labels, name);
            if (label is null)
            {
                try
                {
                    label = await gmail.CreateLabelAsync(name, ct);
                }
                catch (Exception ex) when (CreateError(ex) is { } error)
                {
                    throw new JobRefusedException(
                        $"The merged label {name} was deleted and could not be re-created ({error}); the undo stopped without removing any label.");
                }

                created = true;
                var batch = await db.ActionBatches.SingleAsync(b => b.Id == cursor.BatchId, CancellationToken.None);
                batch.CreatedLabelIds = [.. batch.CreatedLabelIds, label.Id];
                await db.SaveChangesAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                LogSourceRecreated(logger, cursor.UndoOf);
            }

            moved[id] = label.Id;
        }

        if (created)
        {
            catalog.Invalidate();
            labels = await catalog.RefreshAsync(ct);
        }

        return (labels, moved);
    }

    /// <summary>The reason a label create was refused for good; null for a failure the job retries.</summary>
    private static string? CreateError(Exception ex) => ex switch
    {
        GmailLabelExistsException or ArgumentException => ex.Message,
        GoogleApiException { HttpStatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError } api
            when !GmailRetryPolicy.IsRateLimited(api) => api.Error?.Message ?? api.Message,
        _ => null,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-created the deleted source label of merge batch {BatchId} to undo it.")]
    private static partial void LogSourceRecreated(ILogger logger, Guid batchId);
}
