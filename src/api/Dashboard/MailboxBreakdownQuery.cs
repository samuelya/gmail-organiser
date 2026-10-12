using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Senders;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Dashboard;

/// <summary>The Dashboard's "Mailbox mix" (DESIGN §8.2): messages per Gmail category tab and suggestions per mail type, two aggregate queries.</summary>
public static class MailboxBreakdownQuery
{
    public static async Task<MailboxBreakdownDto> LoadAsync(AppDbContext db, CancellationToken ct)
    {
        // The same live per-sender counts the Senders page shows, summed as long so a large mailbox can't overflow in SQL.
        var categories = await db.Senders.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Primary = g.Sum(s => (long)s.PrimaryCount),
                Promotions = g.Sum(s => (long)s.PromotionsCount),
                Social = g.Sum(s => (long)s.SocialCount),
                Updates = g.Sum(s => (long)s.UpdatesCount),
                Forums = g.Sum(s => (long)s.ForumsCount),
            })
            .FirstOrDefaultAsync(ct);

        var byType = await db.Suggestions.AsNoTracking()
            .Where(s => s.Status != SuggestionStatus.Rejected)
            .GroupBy(s => s.MailType)
            .Select(g => new { MailType = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var counts = byType.Where(t => t.MailType is not null).ToDictionary(t => t.MailType!.Value, t => t.Count);

        return new MailboxBreakdownDto(
            new SenderCategoryMixDto(
                (int)(categories?.Primary ?? 0),
                (int)(categories?.Promotions ?? 0),
                (int)(categories?.Social ?? 0),
                (int)(categories?.Updates ?? 0),
                (int)(categories?.Forums ?? 0)),
            [.. Enum.GetValues<MailType>().Select(t => new MailTypeCountDto(t, counts.GetValueOrDefault(t)))],
            byType.SingleOrDefault(t => t.MailType is null)?.Count ?? 0);
    }
}
