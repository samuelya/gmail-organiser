using GmailOrganiser.Analysis;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>Row access shared by the review decisions: keyset chunks and row locks.</summary>
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

    /// <summary>Loads and locks the rows until the transaction ends; uncomposed so the lock clause stays at the top level.</summary>
    private Task<List<SuggestionRow>> LockAsync(Guid[] ids, CancellationToken ct) =>
        db.Suggestions.FromSql($"SELECT * FROM suggestions WHERE id = ANY({ids}) ORDER BY id FOR UPDATE").ToListAsync(ct);
}
