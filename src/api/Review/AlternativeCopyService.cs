using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>What <see cref="AlternativeCopyService.CopyAsync"/> did.</summary>
public enum AlternativeCopyResult
{
    Ok,
    NotFound,
    AnalysisActive,
}

/// <summary>
/// Copies one suggestion's current outcome to similar pending suggestions as alternatives (#562), without the LLM: each copy
/// still goes through accept or discard (<see cref="AlternativeService"/>) and then the normal approve and apply. Nothing
/// changes in Gmail, no decision is recorded and the suggestions are not touched. Targets are locked <c>FOR UPDATE</c> in
/// chunks of <see cref="ReviewService.ChunkSize"/>, one transaction each, and the source <c>FOR KEY SHARE</c> in id order
/// with them, so the copy waits for (and never deadlocks with) accept, discard and a compare run's checkpoint.
/// Endpoints validate first.
/// </summary>
public sealed class AlternativeCopyService(AppDbContext db, TimeProvider time)
{
    /// <summary>
    /// Writes a copy of the source's outcome for every target that is, under lock, pending, from the source's sender and
    /// not the source; it replaces the target's alternative. Everything else is skipped, and so is every target left once
    /// the source is gone. Refused while an analysis run is queued or running.
    /// </summary>
    public async Task<(AlternativeCopyResult Result, AlternativeCopyResponse? Response)> CopyAsync(
        Guid sourceId, Guid[] suggestionIds, CancellationToken ct)
    {
        if (!await db.Suggestions.AnyAsync(s => s.Id == sourceId, ct))
        {
            return (AlternativeCopyResult.NotFound, null);
        }

        if (await db.AnalysisRuns.AnyAsync(r => r.Status == AnalysisRunStatus.Queued || r.Status == AnalysisRunStatus.Running, ct))
        {
            return (AlternativeCopyResult.AnalysisActive, null);
        }

        var targets = suggestionIds.Distinct().ToArray();
        var copied = 0;
        foreach (var chunk in targets.Where(id => id != sourceId).Order().Chunk(ReviewService.ChunkSize))
        {
            if (await CopyChunkAsync(sourceId, chunk, ct) is not { } done)
            {
                break;
            }

            copied += done;
        }

        return (AlternativeCopyResult.Ok, new AlternativeCopyResponse(copied, targets.Length - copied));
    }

    /// <summary>One transaction: copies to the chunk's valid targets; null when the source is gone.</summary>
    private async Task<int?> CopyChunkAsync(Guid sourceId, Guid[] ids, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Rows lock in id order across the three queries (Postgres compares the ids), like every other locker of suggestions.
        var locked = await db.Suggestions.FromSql(
                $"SELECT * FROM suggestions WHERE id = ANY({ids}) AND id < {sourceId} ORDER BY id FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        var source = (await db.Suggestions.FromSql($"SELECT * FROM suggestions WHERE id = {sourceId} FOR KEY SHARE")
            .AsNoTracking().ToListAsync(ct)).SingleOrDefault();
        locked.AddRange(await db.Suggestions.FromSql(
                $"SELECT * FROM suggestions WHERE id = ANY({ids}) AND id > {sourceId} ORDER BY id FOR UPDATE")
            .AsNoTracking().ToListAsync(ct));
        if (source is null)
        {
            return null;
        }

        var valid = locked.Where(s => s.Status == SuggestionStatus.Pending && s.SenderAddress == source.SenderAddress).ToList();
        var messageIds = valid.ConvertAll(s => s.MessageId);
        var messages = await db.Messages.AsNoTracking().Where(m => messageIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, StringComparer.Ordinal, ct);
        valid.RemoveAll(s => !messages.ContainsKey(s.MessageId));
        var validIds = valid.ConvertAll(s => s.Id);
        await db.SuggestionAlternatives.Where(a => validIds.Contains(a.SuggestionId)).ExecuteDeleteAsync(ct);
        var now = time.GetUtcNow();
        db.SuggestionAlternatives.AddRange(valid.Select(s => SuggestionAlternativeRow.CopyOf(source, s, messages[s.MessageId], now)));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return valid.Count;
    }
}
