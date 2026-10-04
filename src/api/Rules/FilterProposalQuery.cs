using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Rules;

/// <summary>
/// Filters to propose from approved senders (DESIGN §6.5): one per sender the user approved an outcome for that no
/// active filter covers yet, most messages first.
/// </summary>
public sealed class FilterProposalQuery(AppDbContext db, SenderPatternService patterns, ISettingsStore settingsStore)
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 50;

    public async Task<PagedDto<FilterProposalDto>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        // Pattern suggestions don't make a pattern (SenderPatternService), so they don't make a proposal either.
        var candidates = await db.Senders.AsNoTracking()
            .Where(s => db.Suggestions.Any(g => g.SenderAddress == s.Address
                && g.Source != SuggestionSource.SenderPattern
                && (g.Status == SuggestionStatus.Approved || g.Status == SuggestionStatus.Applied)))
            .OrderByDescending(s => s.TotalCount)
            .ThenBy(s => s.Address)
            .Select(s => new { s.Address, s.DisplayName, s.TotalCount })
            .ToListAsync(ct);
        var filtered = (await ActiveFromCriteriaAsync(ct)) is { Count: > 0 } froms
            ? candidates.Where(c => !froms.Any(f => f.Contains(c.Address, StringComparison.OrdinalIgnoreCase))).ToList()
            : candidates;

        var settings = await settingsStore.GetAsync(ct);
        var items = new List<FilterProposalDto>();
        foreach (var sender in filtered.Skip((page - 1) * pageSize).Take(pageSize))
        {
            var pattern = await patterns.GetAsync(sender.Address, ct);
            if (pattern.TopicLabel is null)
            {
                continue;
            }

            items.Add(new FilterProposalDto(
                sender.Address, sender.DisplayName, sender.TotalCount, await CommonListIdAsync(sender.Address, ct), pattern,
                Suggest(sender.Address, pattern, settings.DeleteLabelName)));
        }

        return new PagedDto<FilterProposalDto>(items, page, pageSize, filtered.Count);
    }

    /// <summary>
    /// Labels the sender's mail with its topic label and skips the inbox unless it needs action; a to-be-deleted
    /// pattern adds the delete label instead, skips the inbox and leaves mail with attachments alone.
    /// </summary>
    public static FilterSuggestionDto Suggest(string address, SenderPatternDto pattern, string deleteLabelName)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var delete = pattern.ToBeDeleted == true;
        var criteria = new FilterCriteriaDto(address, null, null, null, delete ? "has:attachment" : null, null, null, null, null);
        var action = delete
            ? new FilterActionRequest([deleteLabelName], SkipInbox: true, MarkRead: false)
            : new FilterActionRequest([pattern.TopicLabel!], SkipInbox: pattern.NeedsAction != true, MarkRead: false);
        return new FilterSuggestionDto(criteria, action);
    }

    private async Task<List<string>> ActiveFromCriteriaAsync(CancellationToken ct) =>
        [.. (await db.Filters.AsNoTracking().Where(r => r.DeletedAt == null).ToListAsync(ct))
            .Select(r => r.ReadCriteria().From)
            .OfType<string>()];

    /// <summary>The List-Id every stored message of the sender carries, or null when they differ or some have none.</summary>
    private async Task<string?> CommonListIdAsync(string address, CancellationToken ct)
    {
        var listIds = await db.Messages.AsNoTracking()
            .Where(m => m.FromAddress == address && !m.DeletedInGmail)
            .Select(m => m.ListId)
            .Distinct()
            .Take(2)
            .ToListAsync(ct);
        return listIds is [{ } only] ? only : null;
    }
}
