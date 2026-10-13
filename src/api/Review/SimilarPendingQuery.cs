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
    public async Task<SimilarPendingDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var source = await db.Suggestions.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { s.SenderAddress, s.TopicLabel, s.DocumentTypeLabel, s.NeedsAction, s.ToBeDeleted })
            .SingleOrDefaultAsync(ct);
        if (source is null)
        {
            return null;
        }

        var topic = source.TopicLabel.Trim().ToLower();
        var documentType = source.DocumentTypeLabel;
        var similar = db.Suggestions.AsNoTracking().Where(s => s.Status == SuggestionStatus.Pending
            && s.SenderAddress == source.SenderAddress
            && s.Id != id
            && !(s.TopicLabel.Trim().ToLower() == topic
                && s.DocumentTypeLabel == documentType
                && s.NeedsAction == source.NeedsAction
                && s.ToBeDeleted == source.ToBeDeleted));
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
        var count = rows.Count == 0 ? 0 : rows[0].Total;
        return new SimilarPendingDto(id, count, [.. rows.Select(r => r.Id)], rows.Count(r => r.HasAlternative), count > rows.Count);
    }
}
