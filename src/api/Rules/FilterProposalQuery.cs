using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Rules;

/// <summary>
/// Filters to propose (DESIGN §6.5): the policy proposals (<see cref="PolicyFilterProposalQuery"/>) first, then one per
/// sender without an approved sender or domain policy that the user approved an outcome for and that no active filter
/// covers yet, most messages first.
/// </summary>
public sealed class FilterProposalQuery(
    AppDbContext db, SenderPatternService patterns, ISettingsStore settingsStore, PolicyFilterProposalQuery policyProposals)
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 50;

    /// <param name="source">A <see cref="FilterProposalSources"/> value.</param>
    public async Task<PagedDto<FilterProposalDto>> ListAsync(string source, int page, int pageSize, CancellationToken ct)
    {
        IReadOnlyList<FilterProposalDto> fromPolicies = source == FilterProposalSources.Pattern ? [] : await policyProposals.ListAsync(ct);
        var skip = (page - 1) * pageSize;
        List<FilterProposalDto> items = [.. fromPolicies.Skip(skip).Take(pageSize)];
        if (source == FilterProposalSources.Policy)
        {
            return new PagedDto<FilterProposalDto>(items, page, pageSize, fromPolicies.Count);
        }

        var (total, fromPatterns) = await PatternsAsync(Math.Max(0, skip - fromPolicies.Count), pageSize - items.Count, ct);
        return new PagedDto<FilterProposalDto>([.. items, .. fromPatterns], page, pageSize, fromPolicies.Count + total);
    }

    private async Task<(int Total, List<FilterProposalDto> Items)> PatternsAsync(int skip, int take, CancellationToken ct)
    {
        // Pattern and Stage-0 suggestions don't make a pattern (SenderPatternService), so they don't make a proposal either.
        // A sender an approved sender or domain policy decides gets its filter from the policy.
        var candidates = Uncovered(
            db.Senders.AsNoTracking().Where(s => db.Suggestions.Any(g => g.SenderAddress == s.Address
                && g.Source != SuggestionSource.SenderPattern
                && g.Source != SuggestionSource.Stage0
                && (g.Status == SuggestionStatus.Approved || g.Status == SuggestionStatus.Applied))
                && !db.SenderPolicies.Any(p => p.Status == PolicyStatus.Approved
                    && ((p.Scope == PolicyScope.Sender && p.ScopeKey == s.CanonicalAddress)
                        || (p.Scope == PolicyScope.Domain && p.ScopeKey == s.CanonicalDomain)))),
            await ActiveFromTermsAsync(db, ct));
        var total = await candidates.CountAsync(ct);
        if (take <= 0)
        {
            return (total, []);
        }

        var senders = await candidates
            .OrderByDescending(s => s.TotalCount)
            .ThenBy(s => s.Address)
            .Skip(skip)
            .Take(take)
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
                Suggest(sender.Address, pattern, settings.DeleteLabelName, allowlist), "sender:" + sender.Address));
        }

        return (total, items);
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

    /// <summary>The <see cref="FilterCriteriaMapping.FromTerms"/> of every active filter's <c>from</c>.</summary>
    public static async Task<List<string>> ActiveFromTermsAsync(AppDbContext db, CancellationToken ct) =>
        [.. (await ActiveCriteriaAsync(db, ct))
            .Select(c => c.From)
            .OfType<string>()
            .SelectMany(f => FilterCriteriaMapping.FromTerms(f) ?? [])
            .Distinct(StringComparer.Ordinal)];

    /// <summary>The lower-case List-Ids of every active filter whose <c>query</c> is <c>list:&lt;id&gt;</c>.</summary>
    public static async Task<List<string>> ActiveListIdsAsync(AppDbContext db, CancellationToken ct) =>
        [.. (await ActiveCriteriaAsync(db, ct))
            .Select(c => c.Query?.Trim())
            .Where(q => q is { Length: > 5 } && q.StartsWith("list:", StringComparison.OrdinalIgnoreCase) && !q.Any(char.IsWhiteSpace))
            .Select(q => q![5..].Trim('<', '>').ToLowerInvariant())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)];

    private static async Task<IEnumerable<GmailFilterCriteria>> ActiveCriteriaAsync(AppDbContext db, CancellationToken ct) =>
        (await db.Filters.AsNoTracking().Where(r => r.DeletedAt == null).ToListAsync(ct)).Select(r => r.ReadCriteria());
}
