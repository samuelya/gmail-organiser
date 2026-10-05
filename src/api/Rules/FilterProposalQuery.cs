using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
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
        // Pattern and Stage-0 suggestions don't make a pattern (SenderPatternService), so they don't make a proposal either.
        var candidates = Uncovered(
            db.Senders.AsNoTracking().Where(s => db.Suggestions.Any(g => g.SenderAddress == s.Address
                && g.Source != SuggestionSource.SenderPattern
                && g.Source != SuggestionSource.Stage0
                && (g.Status == SuggestionStatus.Approved || g.Status == SuggestionStatus.Applied))),
            await ActiveFromTermsAsync(ct));
        var total = await candidates.CountAsync(ct);
        var senders = await candidates
            .OrderByDescending(s => s.TotalCount)
            .ThenBy(s => s.Address)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new { s.Address, s.DisplayName, s.TotalCount })
            .ToListAsync(ct);

        string[] addresses = [.. senders.Select(s => s.Address)];
        var found = await patterns.GetManyAsync(addresses, ct);
        var listIds = await patterns.CommonListIdsAsync(addresses, ct);
        var settings = await settingsStore.GetAsync(ct);
        var allowlist = await AllowlistLoader.LoadAsync(db, settings, addresses, ct);
        var items = new List<FilterProposalDto>();
        foreach (var sender in senders)
        {
            // Null only when the sender's approvals were undone between the queries above.
            var pattern = found[sender.Address];
            if (pattern.TopicLabel is null)
            {
                continue;
            }

            items.Add(new FilterProposalDto(
                sender.Address, sender.DisplayName, sender.TotalCount, listIds.GetValueOrDefault(sender.Address), pattern,
                Suggest(sender.Address, pattern, settings.DeleteLabelName, allowlist)));
        }

        return new PagedDto<FilterProposalDto>(items, page, pageSize, total);
    }

    /// <summary>
    /// Labels the sender's mail with its topic label and skips the inbox unless it needs action; a to-be-deleted
    /// pattern adds the delete label instead, skips the inbox and leaves mail with attachments alone. An allowlisted
    /// sender or domain is protected (MessageProtection), so it never gets the delete label: its proposal is the topic label.
    /// </summary>
    public static FilterSuggestionDto Suggest(string address, SenderPatternDto pattern, string deleteLabelName, Allowlist allowlist)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(allowlist);
        var delete = pattern.ToBeDeleted == true && allowlist.Reason(address) is null;
        var criteria = new FilterCriteriaDto(address, null, null, null, delete ? "has:attachment" : null, null, null, null, null);
        var action = delete
            ? new FilterActionRequest([deleteLabelName], SkipInbox: true, MarkRead: false)
            : new FilterActionRequest([pattern.TopicLabel!], SkipInbox: pattern.NeedsAction != true, MarkRead: false);
        return new FilterSuggestionDto(criteria, action);
    }

    /// <summary>
    /// <paramref name="senders"/> minus those an active filter's <c>from</c> matches: an exact address, or an
    /// <c>@domain</c> with its subdomains (<see cref="FilterCriteriaMapping.FromTerms"/>). A <c>from</c> that is not an
    /// address list hides nobody.
    /// </summary>
    private static IQueryable<SenderRow> Uncovered(IQueryable<SenderRow> senders, IReadOnlyList<string> terms)
    {
        string[] exact = [.. terms.Where(t => t[0] != '@')];
        if (exact.Length > 0)
        {
            senders = senders.Where(s => !exact.Contains(s.Address));
        }

        foreach (var domain in terms.Where(t => t[0] == '@'))
        {
            var subdomain = "." + domain[1..];
            senders = senders.Where(s => !s.Address.EndsWith(domain) && !s.Address.EndsWith(subdomain));
        }

        return senders;
    }

    private async Task<List<string>> ActiveFromTermsAsync(CancellationToken ct) =>
        [.. (await db.Filters.AsNoTracking().Where(r => r.DeletedAt == null).ToListAsync(ct))
            .Select(r => r.ReadCriteria().From)
            .OfType<string>()
            .SelectMany(f => FilterCriteriaMapping.FromTerms(f) ?? [])
            .Distinct(StringComparer.Ordinal)];
}
