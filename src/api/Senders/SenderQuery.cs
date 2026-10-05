using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Senders;

public enum SenderSort
{
    Total,
    LastSeen,
    Address,
    Analysed,

    /// <summary>Unread ratio (<c>unread_count / total_count</c>); senders without messages sort last either way.</summary>
    Unread,
}

/// <summary>A validated senders list request.</summary>
public sealed record SenderQuery(string? Search, int Page, int PageSize, SenderSort Sort, bool Descending)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxSearchLength = 200;

    /// <summary>Keeps <c>(page - 1) * pageSize</c> inside an <see cref="int"/>.</summary>
    public const int MaxPage = 1_000_000;

    private const char LikeEscape = '\\';

    /// <summary>Only senders with this <c>allowlisted</c> flag; null for all. Stub rows (never fetched) are included.</summary>
    public bool? Allowlisted { get; init; }

    /// <summary>Only senders of these kinds; empty for all.</summary>
    public IReadOnlyList<SenderKind> Kinds { get; init; } = [];

    /// <summary>Parses the query string values; <paramref name="errors"/> holds the field errors when it returns null.</summary>
    /// <param name="kinds">The repeatable <c>kind</c> values, each one of <c>human|bulk|mixed|unknown</c>.</param>
    public static SenderQuery? Parse(
        string? search, int? page, int? pageSize, string? sort, string? dir, out Dictionary<string, string[]> errors,
        IReadOnlyList<string>? kinds = null)
    {
        errors = [];
        var p = page ?? 1;
        if (p is < 1 or > MaxPage)
        {
            errors["page"] = [$"Must be between 1 and {MaxPage}."];
        }

        var size = pageSize ?? DefaultPageSize;
        if (size is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"Must be between 1 and {MaxPageSize}."];
        }

        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (term is { Length: > MaxSearchLength } || (term is not null && term.Any(char.IsControl)))
        {
            errors["search"] = [$"Must be at most {MaxSearchLength} characters, without control characters."];
        }

        SenderSort? sortBy = sort is null ? SenderSort.Total : sort.ToLowerInvariant() switch
        {
            "total" => SenderSort.Total,
            "lastseen" => SenderSort.LastSeen,
            "address" => SenderSort.Address,
            "analysed" => SenderSort.Analysed,
            "unread" => SenderSort.Unread,
            _ => null,
        };
        if (sortBy is null)
        {
            errors["sort"] = ["Must be one of total, lastSeen, address, analysed, unread."];
        }

        var kindValues = (kinds ?? []).Select(k => k?.Trim().ToLowerInvariant() switch
        {
            "human" => SenderKind.Human,
            "bulk" => SenderKind.Bulk,
            "mixed" => SenderKind.Mixed,
            "unknown" => SenderKind.Unknown,
            _ => (SenderKind?)null,
        }).ToList();
        if (kindValues.Contains(null))
        {
            errors["kind"] = ["Each must be one of human, bulk, mixed, unknown."];
        }

        bool? descending = dir is null ? true : dir.ToLowerInvariant() switch
        {
            "asc" => false,
            "desc" => true,
            _ => null,
        };
        if (descending is null)
        {
            errors["dir"] = ["Must be asc or desc."];
        }

        return errors.Count > 0
            ? null
            : new SenderQuery(term, p, size, sortBy!.Value, descending!.Value)
            {
                Kinds = [.. kindValues.Select(k => k!.Value).Distinct()],
            };
    }

    /// <param name="allowlistedDomains"><c>protection.allowlistedDomains</c>, for <see cref="SenderDto.AllowlistedByDomain"/>.</param>
    public async Task<PagedDto<SenderDto>> ExecuteAsync(AppDbContext db, IReadOnlyList<string> allowlistedDomains, CancellationToken ct)
    {
        var senders = Filter(db.Senders.AsNoTracking(), Search);
        if (Allowlisted is { } allowlisted)
        {
            senders = senders.Where(s => s.Allowlisted == allowlisted);
        }

        if (Kinds.Count > 0)
        {
            var kinds = Kinds.ToList();
            senders = senders.Where(s => kinds.Contains(s.Kind));
        }

        var total = await senders.LongCountAsync(ct);
        var rows = await Order(senders).Skip((Page - 1) * PageSize).Take(PageSize).ToListAsync(ct);
        var fetchJobs = rows.Count == 0 ? [] : await ActiveSenderFetchJobsAsync(db, ct);

        return new PagedDto<SenderDto>(rows.ConvertAll(s => ToDto(s, allowlistedDomains, fetchJobs)), Page, PageSize, total);
    }

    /// <summary>One sender with its active fetch job.</summary>
    public static async Task<SenderDto> ToDtoAsync(
        SenderRow sender, IReadOnlyList<string> allowlistedDomains, AppDbContext db, CancellationToken ct) =>
        ToDto(sender, allowlistedDomains, await ActiveSenderFetchJobsAsync(db, ct));

    /// <summary>The sender without its fetch job.</summary>
    public static SenderDto ToDto(SenderRow s, IReadOnlyList<string> allowlistedDomains) => ToDto(s, allowlistedDomains, []);

    private static SenderDto ToDto(
        SenderRow s, IReadOnlyList<string> allowlistedDomains, List<(string Target, JobDto Job)> fetchJobs) => new(
        s.Address, s.Domain, s.DisplayName, s.TotalCount, s.AnalysedCount, s.AppliedCount, s.LastSeenAt, s.Allowlisted,
        Allowlist.CoversDomain(allowlistedDomains, s.Domain),
        fetchJobs.Find(j => j.Target.Equals(s.Address, StringComparison.OrdinalIgnoreCase)
            || j.Target.Equals(s.Domain, StringComparison.OrdinalIgnoreCase)).Job,
        s.UnsubscribedAt,
        s.CanonicalAddress, s.CanonicalDomain, s.IsRelay, s.Kind, s.UnreadCount, s.RepliedCount, s.FirstSeenAt,
        s.ListUnsubscribeCount,
        new SenderCategoryMixDto(s.PrimaryCount, s.PromotionsCount, s.SocialCount, s.UpdatesCount, s.ForumsCount));

    /// <summary>
    /// Senders whose address, domain, canonical address or domain, or display name contains <paramref name="search"/>
    /// (all when null).
    /// </summary>
    public static IQueryable<SenderRow> Filter(IQueryable<SenderRow> senders, string? search)
    {
        if (search is null)
        {
            return senders;
        }

        var pattern = $"%{EscapeLike(search)}%";
        return senders.Where(s =>
            EF.Functions.ILike(s.Address, pattern, LikeEscape.ToString())
            || EF.Functions.ILike(s.Domain, pattern, LikeEscape.ToString())
            || EF.Functions.ILike(s.CanonicalAddress, pattern, LikeEscape.ToString())
            || EF.Functions.ILike(s.CanonicalDomain, pattern, LikeEscape.ToString())
            || (s.DisplayName != null && EF.Functions.ILike(s.DisplayName, pattern, LikeEscape.ToString())));
    }

    /// <summary>Escapes the LIKE wildcards so the search term matches literally.</summary>
    public static string EscapeLike(string term) => term
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);

    /// <summary>The chosen sort, always followed by the address so pages are stable.</summary>
    private IOrderedQueryable<SenderRow> Order(IQueryable<SenderRow> senders) => (Sort, Descending) switch
    {
        (SenderSort.Address, false) => senders.OrderBy(s => s.Address),
        (SenderSort.Address, true) => senders.OrderByDescending(s => s.Address),
        (SenderSort.Total, false) => senders.OrderBy(s => s.TotalCount).ThenBy(s => s.Address),
        (SenderSort.Total, true) => senders.OrderByDescending(s => s.TotalCount).ThenBy(s => s.Address),
        (SenderSort.Analysed, false) => senders.OrderBy(s => s.AnalysedCount).ThenBy(s => s.Address),
        (SenderSort.Analysed, true) => senders.OrderByDescending(s => s.AnalysedCount).ThenBy(s => s.Address),
        // Senders never seen sort last either way.
        (SenderSort.LastSeen, false) => senders.OrderBy(s => s.LastSeenAt == null).ThenBy(s => s.LastSeenAt).ThenBy(s => s.Address),
        (SenderSort.LastSeen, true) => senders.OrderBy(s => s.LastSeenAt == null).ThenByDescending(s => s.LastSeenAt).ThenBy(s => s.Address),
        // unread_count::float / NULLIF(total_count, 0), nulls last either way.
        (SenderSort.Unread, false) => senders.OrderBy(s => s.TotalCount == 0)
            .ThenBy(s => s.TotalCount == 0 ? (double?)null : (double)s.UnreadCount / s.TotalCount).ThenBy(s => s.Address),
        (SenderSort.Unread, true) => senders.OrderBy(s => s.TotalCount == 0)
            .ThenByDescending(s => s.TotalCount == 0 ? (double?)null : (double)s.UnreadCount / s.TotalCount).ThenBy(s => s.Address),
        _ => throw new InvalidOperationException($"Unknown sort {Sort}."),
    };

    /// <summary>One query for the whole page: the active per-sender fetch jobs with their cursor target.</summary>
    private static async Task<List<(string Target, JobDto Job)>> ActiveSenderFetchJobsAsync(AppDbContext db, CancellationToken ct)
    {
        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => j.Type == FetchJobTypes.Sender && JobRow.Active.Contains(j.Status) && j.Cursor != null)
            .ToListAsync(ct);
        return [.. jobs.Select(j => (Target: ReadTarget(j.Cursor!), Job: j)).Where(t => t.Target is not null).Select(t => (t.Target!, t.Job.ToDto()))];
    }

    private static string? ReadTarget(string cursor)
    {
        using var doc = JsonDocument.Parse(cursor);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("target", out var target)
            && target.ValueKind == JsonValueKind.String
                ? target.GetString()
                : null;
    }
}
