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
/// chunks of <see cref="ReviewService.ChunkSize"/>, one transaction each, and the source <c>FOR SHARE</c> in id order
/// with them, so the copy waits for (and never deadlocks with) accept, discard and a compare run's checkpoint.
/// Endpoints validate first.
/// </summary>
public sealed class AlternativeCopyService(AppDbContext db, TimeProvider time)
{
    /// <summary>
    /// Writes a copy of the source's outcome for every target that is, under lock, pending, from the source's sender, not
    /// the source and whose message is not deleted in Gmail; it replaces the target's alternative. Every target gets the
    /// outcome the first chunk read: once the source is changed or gone, the targets left are skipped, like every invalid one.
    /// Refused while an analysis run is queued or running, checked again in each chunk's transaction (a later chunk stops
    /// and skips the rest). Run start takes no lock, so a run enqueued but not yet committed is not seen; a compare run
    /// then replaces the copies (later-created wins).
    /// </summary>
    public async Task<(AlternativeCopyResult Result, AlternativeCopyResponse? Response)> CopyAsync(
        Guid sourceId, Guid[] suggestionIds, CancellationToken ct)
    {
        if (!await db.Suggestions.AnyAsync(s => s.Id == sourceId, ct))
        {
            return (AlternativeCopyResult.NotFound, null);
        }

        if (await AnalysisActiveAsync(ct))
        {
            return (AlternativeCopyResult.AnalysisActive, null);
        }

        var targets = suggestionIds.Distinct().ToArray();
        var copied = 0;
        Snapshot? snapshot = null;
        foreach (var chunk in targets.Where(id => id != sourceId).Order().Chunk(ReviewService.ChunkSize))
        {
            var (status, done, read) = await CopyChunkAsync(sourceId, snapshot, chunk, ct);
            if (status == ChunkStatus.AnalysisActive && snapshot is null)
            {
                return (AlternativeCopyResult.AnalysisActive, null);
            }

            if (status != ChunkStatus.Copied)
            {
                break;
            }

            snapshot = read;
            copied += done;
        }

        return (AlternativeCopyResult.Ok, new AlternativeCopyResponse(copied, targets.Length - copied));
    }

    private enum ChunkStatus
    {
        Copied,
        SourceChanged,
        AnalysisActive,
    }

    /// <summary>The source as the first chunk read it, and its row version (<c>xmin</c>), which any later update changes.</summary>
    private sealed record Snapshot(SuggestionRow Source, string Version);

    private Task<bool> AnalysisActiveAsync(CancellationToken ct) =>
        db.AnalysisRuns.AnyAsync(r => r.Status == AnalysisRunStatus.Queued || r.Status == AnalysisRunStatus.Running, ct);

    /// <summary>One transaction: copies <paramref name="snapshot"/> (the source as read now, on the first chunk) to the chunk's valid targets.</summary>
    private async Task<(ChunkStatus Status, int Copied, Snapshot? Read)> CopyChunkAsync(
        Guid sourceId, Snapshot? snapshot, Guid[] ids, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Rows lock in id order across the three queries (Postgres compares the ids), like every other locker of suggestions.
        var locked = await db.Suggestions.FromSql(
                $"SELECT * FROM suggestions WHERE id = ANY({ids}) AND id < {sourceId} ORDER BY id FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        // FOR SHARE: the source can't change while this chunk reads it; its version tells whether it changed since the first chunk.
        var source = (await db.Suggestions.FromSql($"SELECT * FROM suggestions WHERE id = {sourceId} FOR SHARE")
            .AsNoTracking().ToListAsync(ct)).SingleOrDefault();
        var version = await db.Database.SqlQuery<string>($"SELECT xmin::text AS \"Value\" FROM suggestions WHERE id = {sourceId}")
            .SingleOrDefaultAsync(ct);
        locked.AddRange(await db.Suggestions.FromSql(
                $"SELECT * FROM suggestions WHERE id = ANY({ids}) AND id > {sourceId} ORDER BY id FOR UPDATE")
            .AsNoTracking().ToListAsync(ct));
        if (source is null || version is null || (snapshot is not null && snapshot.Version != version))
        {
            return (ChunkStatus.SourceChanged, 0, null);
        }

        if (await AnalysisActiveAsync(ct))
        {
            return (ChunkStatus.AnalysisActive, 0, null);
        }

        snapshot ??= new Snapshot(source, version);
        var valid = locked.Where(s => s.Status == SuggestionStatus.Pending && s.SenderAddress == source.SenderAddress).ToList();
        var messageIds = valid.ConvertAll(s => s.MessageId);
        var labels = await db.Messages.AsNoTracking()
            .Where(m => messageIds.Contains(m.Id) && !m.DeletedInGmail)
            .Select(m => new { m.Id, m.LabelIds })
            .ToDictionaryAsync(m => m.Id, m => m.LabelIds, StringComparer.Ordinal, ct);
        valid.RemoveAll(s => !labels.ContainsKey(s.MessageId));
        var validIds = valid.ConvertAll(s => s.Id);
        await db.SuggestionAlternatives.Where(a => validIds.Contains(a.SuggestionId)).ExecuteDeleteAsync(ct);
        var now = time.GetUtcNow();
        db.SuggestionAlternatives.AddRange(valid.Select(s => SuggestionAlternativeRow.CopyOf(snapshot.Source, s, labels[s.MessageId], now)));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return (ChunkStatus.Copied, valid.Count, snapshot);
    }
}
