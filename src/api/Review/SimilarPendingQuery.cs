using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>
/// Similar pending suggestions of a source suggestion (#560): newest first, at most
/// <see cref="SettingsValidation.MaxAnalysisDefaultCount"/>; <see cref="Count"/> is the total before the cap,
/// <see cref="WithAlternative"/> how many of the returned ids already have an alternative.
/// </summary>
public sealed record SimilarPendingDto(Guid SourceId, int Count, Guid[] SuggestionIds, int WithAlternative, bool Truncated);

/// <summary>
/// The pending suggestions of the source's sender, across its cards, whose outcome differs from the source's: the ones an
/// accepted re-analysis could apply to. Read only: no locks, writes or Gmail calls.
/// </summary>
public sealed class SimilarPendingQuery(AppDbContext db)
{
    /// <summary>The similar pending suggestions of <paramref name="id"/>, or null when it does not exist.</summary>
    /// <remarks>
    /// Outcome equality matches memory's (#522): topic and document type trimmed and case-insensitive, extras as arrays
    /// (canonical, so array equality is outcome equality), mail type, needsAction and toBeDeleted. The source row is joined
    /// in the same query so both sides are normalised by Postgres.
    /// </remarks>
    public async Task<SimilarPendingDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var similar =
            from src in db.Suggestions.AsNoTracking()
            where src.Id == id
            from s in db.Suggestions.AsNoTracking()
            where s.Status == SuggestionStatus.Pending
                && s.SenderAddress == src.SenderAddress
                && s.Id != src.Id
                && !(s.TopicLabel.Trim().ToLower() == src.TopicLabel.Trim().ToLower()
                    && s.DocumentTypeLabel!.Trim().ToLower() == src.DocumentTypeLabel!.Trim().ToLower()
                    && s.ExtraLabels == src.ExtraLabels
                    && s.MailType == src.MailType
                    && s.NeedsAction == src.NeedsAction
                    && s.ToBeDeleted == src.ToBeDeleted)
            select s;
        var max = SettingsValidation.MaxAnalysisDefaultCount;
        var rows = await similar
            .Join(db.Messages, s => s.MessageId, m => m.Id, (s, m) => new { s.Id, m.InternalDate })
            .OrderByDescending(r => r.InternalDate).ThenBy(r => r.Id)
            .Take(max)
            .Select(r => new
            {
                r.Id,
                HasAlternative = db.SuggestionAlternatives.Any(a => a.SuggestionId == r.Id),
                Total = similar.Count(),
            })
            .ToListAsync(ct);
        if (rows.Count == 0 && !await db.Suggestions.AnyAsync(s => s.Id == id, ct))
        {
            return null;
        }

        var count = rows.Count == 0 ? 0 : rows[0].Total;
        return new SimilarPendingDto(id, count, [.. rows.Select(r => r.Id)], rows.Count(r => r.HasAlternative), count > rows.Count);
    }
}
