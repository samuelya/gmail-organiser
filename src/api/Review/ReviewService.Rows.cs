using GmailOrganiser.Analysis;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>Row access and lookups shared by the review decisions: keyset chunks, row locks, label lookups.</summary>
public sealed partial class ReviewService
{
    /// <summary>The matching ids in keyset chunks of <see cref="ChunkSize"/>; rows left pending are not seen twice.</summary>
    private static async IAsyncEnumerable<Guid[]> ChunksAsync(
        IQueryable<SuggestionRow> candidates, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Guid? after = null;
        while (true)
        {
            var page = after is { } last ? candidates.Where(s => s.Id.CompareTo(last) > 0) : candidates;
            var ids = await page.OrderBy(s => s.Id).Select(s => s.Id).Take(ChunkSize).ToArrayAsync(ct);
            if (ids.Length == 0)
            {
                yield break;
            }

            yield return ids;
            after = ids[^1];
        }
    }

    /// <summary>
    /// A group's chunks. Inside a caller's transaction (accepting a Claude verdict) each chunk's locks last until the caller
    /// commits, so every pending member is locked up front in id order, before any chunk locks a policy: suggestions, then
    /// policies, the order a bulk approve takes them in, so the two never deadlock (#426).
    /// </summary>
    private async IAsyncEnumerable<Guid[]> GroupChunksAsync(
        IQueryable<SuggestionRow> candidates, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            await foreach (var chunk in ChunksAsync(candidates, ct))
            {
                yield return chunk;
            }

            yield break;
        }

        var ids = await candidates.OrderBy(s => s.Id).Select(s => s.Id).ToArrayAsync(ct);
        await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM suggestions WHERE id = ANY({ids}) ORDER BY id FOR UPDATE")
            .ToListAsync(ct);
        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            yield return chunk;
        }
    }

    /// <summary>Loads and locks the rows until the transaction ends; uncomposed so the lock clause stays at the top level.</summary>
    private Task<List<SuggestionRow>> LockAsync(Guid[] ids, CancellationToken ct) =>
        db.Suggestions.FromSql($"SELECT * FROM suggestions WHERE id = ANY({ids}) ORDER BY id FOR UPDATE").ToListAsync(ct);

    /// <summary>Whether Gmail lacks the label; null without a Gmail connection or when the label list cannot be loaded.</summary>
    private async Task<bool?> IsNewLabelAsync(string label, CancellationToken ct)
    {
        try
        {
            return await labels.FindByNameAsync(label, ct) is null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped the document-type label of suggestion {Id}: it is the edited topic label.")]
    private static partial void LogDocumentTypeDropped(ILogger logger, Guid id);
}
