using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Senders;

/// <summary>
/// A validated noisy-senders request: senders grouped by canonical address with many messages, mostly unread, never
/// replied to and not allowlisted, found from the Stage-0 stats alone (no LLM).
/// </summary>
/// <param name="DormantDays">Only senders last seen more than this many days ago; null for all.</param>
public sealed record NoisySenderQuery(int MinMessages, double MinUnreadRatio, int? DormantDays, string? Search, int Page, int PageSize)
{
    public const int DefaultMinMessages = 10;
    public const double DefaultMinUnreadRatio = 0.9;
    public const int MaxMinMessages = 100_000;
    public const int MaxDormantDays = 3650;
    public const int MaxPageSize = 100;

    /// <summary>The raw addresses listed per canonical sender.</summary>
    public const int MaxAddresses = 20;

    /// <summary>Parses the query string values; <paramref name="errors"/> holds the field errors when it returns null.</summary>
    public static NoisySenderQuery? Parse(
        int? minMessages, double? minUnreadRatio, int? dormantDays, string? search, int? page, int? pageSize,
        out Dictionary<string, string[]> errors)
    {
        // SenderQuery validates search, page and pageSize; only the page size cap is tighter here.
        var paging = SenderQuery.Parse(search, page, pageSize, null, null, out errors);
        if (errors.ContainsKey("pageSize") || pageSize > MaxPageSize)
        {
            errors["pageSize"] = [$"Must be between 1 and {MaxPageSize}."];
        }

        var min = minMessages ?? DefaultMinMessages;
        if (min is < 1 or > MaxMinMessages)
        {
            errors["minMessages"] = [$"Must be between 1 and {MaxMinMessages}."];
        }

        var ratio = minUnreadRatio ?? DefaultMinUnreadRatio;
        if (ratio is not (>= 0 and <= 1))
        {
            errors["minUnreadRatio"] = ["Must be between 0 and 1."];
        }

        if (dormantDays is < 1 or > MaxDormantDays)
        {
            errors["dormantDays"] = [$"Must be between 1 and {MaxDormantDays}."];
        }

        return errors.Count > 0 ? null : new NoisySenderQuery(min, ratio, dormantDays, paging!.Search, paging.Page, paging.PageSize);
    }

    /// <param name="allowlistedDomains"><c>protection.allowlistedDomains</c>: a group with a raw or canonical domain under one is excluded.</param>
    public async Task<PagedDto<NoisySenderDto>> ExecuteAsync(
        AppDbContext db, IReadOnlyList<string> allowlistedDomains, TimeProvider time, CancellationToken ct)
    {
        var groups = Groups(db, allowlistedDomains);
        if (DormantDays is { } days)
        {
            var cutoff = time.GetUtcNow().AddDays(-days);
            groups = groups.Where(g => g.LastSeenAt < cutoff);
        }

        var total = await groups.LongCountAsync(ct);
        var rows = await groups.OrderByDescending(g => g.TotalCount).ThenBy(g => g.CanonicalAddress)
            .Skip((Page - 1) * PageSize).Take(PageSize).ToListAsync(ct);
        var addresses = rows.Count == 0 ? [] : await AddressesAsync(db, [.. rows.Select(r => r.CanonicalAddress)], ct);

        return new PagedDto<NoisySenderDto>(
            rows.ConvertAll(g => new NoisySenderDto(
                g.CanonicalAddress, g.CanonicalDomain, g.DisplayName, addresses.GetValueOrDefault(g.CanonicalAddress, []),
                g.TotalCount, g.UnreadCount, (double)g.UnreadCount / g.TotalCount, g.ListUnsubscribeCount,
                g.Mixed > 0 ? SenderKind.Mixed : g.Bulk > 0 ? SenderKind.Bulk : SenderKind.Unknown,
                g.FirstSeenAt, g.LastSeenAt,
                new SenderCategoryMixDto(g.PrimaryCount, g.PromotionsCount, g.SocialCount, g.UpdatesCount, g.ForumsCount),
                g.UnsubscribedAt, HasApprovedPolicy: false)),
            Page, PageSize, total);
    }

    /// <summary>
    /// The noisy groups in SQL (<c>GROUP BY canonical_address</c>). Sums cover only rows whose stats were computed; one
    /// excluded raw row (human, replied to, allowlisted) excludes the whole group, so a relay never hides a protected sender.
    /// </summary>
    private IQueryable<Group> Groups(AppDbContext db, IReadOnlyList<string> allowlistedDomains)
    {
        // As CleanUpQuery: an IDN entry also matches its Unicode form; entries hold no '@'.
        var domains = Allowlist.SqlForms(allowlistedDomains);
        var senders = db.Senders.AsNoTracking();
        if (Search is not null)
        {
            var matching = SenderQuery.Filter(db.Senders, Search).Select(s => s.CanonicalAddress);
            senders = senders.Where(s => matching.Contains(s.CanonicalAddress));
        }

        var (min, ratio) = (MinMessages, MinUnreadRatio);
        return senders
            .Select(s => new
            {
                Row = s,
                Stats = s.StatsAt != null,
                Excluded = s.Allowlisted || s.RepliedCount > 0 || s.Kind == SenderKind.Human
                    || domains.Any(d => s.Domain == d || s.Domain.EndsWith("." + d)
                        || s.CanonicalDomain == d || s.CanonicalDomain.EndsWith("." + d)),
            })
            .GroupBy(s => s.Row.CanonicalAddress)
            .Select(g => new Group
            {
                CanonicalAddress = g.Key,
                CanonicalDomain = g.Max(s => s.Row.CanonicalDomain)!,
                DisplayName = g.Max(s => s.Row.DisplayName),
                Excluded = g.Sum(s => s.Excluded ? 1 : 0),
                StatsRows = g.Sum(s => s.Stats ? 1 : 0),
                TotalCount = g.Sum(s => s.Stats ? s.Row.TotalCount : 0),
                UnreadCount = g.Sum(s => s.Stats ? s.Row.UnreadCount : 0),
                ListUnsubscribeCount = g.Sum(s => s.Stats ? s.Row.ListUnsubscribeCount : 0),
                PrimaryCount = g.Sum(s => s.Stats ? s.Row.PrimaryCount : 0),
                PromotionsCount = g.Sum(s => s.Stats ? s.Row.PromotionsCount : 0),
                SocialCount = g.Sum(s => s.Stats ? s.Row.SocialCount : 0),
                UpdatesCount = g.Sum(s => s.Stats ? s.Row.UpdatesCount : 0),
                ForumsCount = g.Sum(s => s.Stats ? s.Row.ForumsCount : 0),
                Mixed = g.Sum(s => s.Stats && s.Row.Kind == SenderKind.Mixed ? 1 : 0),
                Bulk = g.Sum(s => s.Stats && s.Row.Kind == SenderKind.Bulk ? 1 : 0),
                FirstSeenAt = g.Min(s => s.Row.FirstSeenAt),
                LastSeenAt = g.Max(s => s.Row.LastSeenAt),
                UnsubscribedAt = g.Max(s => s.Row.UnsubscribedAt),
            })
            // The CASE keeps the division safe: SQL does not promise to evaluate TotalCount >= min first.
            .Where(g => g.Excluded == 0 && g.StatsRows > 0 && g.TotalCount >= min
                && (double)g.UnreadCount / (g.TotalCount == 0 ? 1 : g.TotalCount) >= ratio);
    }

    /// <summary>One query for the page: each canonical sender's raw addresses, highest volume first.</summary>
    private static async Task<Dictionary<string, IReadOnlyList<string>>> AddressesAsync(
        AppDbContext db, List<string> canonical, CancellationToken ct)
    {
        var rows = await db.Senders.AsNoTracking()
            .Where(s => canonical.Contains(s.CanonicalAddress))
            .GroupBy(s => s.CanonicalAddress)
            .Select(g => new
            {
                g.Key,
                Addresses = g.OrderByDescending(s => s.TotalCount).ThenBy(s => s.Address).Select(s => s.Address).Take(MaxAddresses).ToList(),
            })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => (IReadOnlyList<string>)r.Addresses, StringComparer.Ordinal);
    }

    private sealed class Group
    {
        public string CanonicalAddress { get; init; } = "";
        public string CanonicalDomain { get; init; } = "";
        public string? DisplayName { get; init; }
        public int Excluded { get; init; }
        public int StatsRows { get; init; }
        public int TotalCount { get; init; }
        public int UnreadCount { get; init; }
        public int ListUnsubscribeCount { get; init; }
        public int PrimaryCount { get; init; }
        public int PromotionsCount { get; init; }
        public int SocialCount { get; init; }
        public int UpdatesCount { get; init; }
        public int ForumsCount { get; init; }
        public int Mixed { get; init; }
        public int Bulk { get; init; }
        public DateTimeOffset? FirstSeenAt { get; init; }
        public DateTimeOffset? LastSeenAt { get; init; }
        public DateTimeOffset? UnsubscribedAt { get; init; }
    }
}
