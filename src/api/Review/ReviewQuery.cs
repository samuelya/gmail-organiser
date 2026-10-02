using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Senders;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>The review sender list and one sender's suggestions grouped by <see cref="SuggestionRow.GroupKey"/>.</summary>
public sealed class ReviewQuery(AppDbContext db)
{
    /// <summary>Most members listed for one group.</summary>
    public const int MaxMembers = 500;

    /// <summary>Most members listed in one detail response; every group still lists its newest member.</summary>
    public const int MaxResponseMembers = 2000;

    public const int DefaultGroupPageSize = 20;
    public const int MaxGroupPageSize = 50;

    /// <summary>Parses <c>pending|approved|rejected</c> (default pending); null when invalid.</summary>
    public static SuggestionStatus? ParseStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "pending" => SuggestionStatus.Pending,
        "approved" => SuggestionStatus.Approved,
        "rejected" => SuggestionStatus.Rejected,
        _ => null,
    };

    /// <summary>
    /// Senders with at least one suggestion in <paramref name="status"/>, with their counts per status, the most
    /// suggestions in that status first (pending by default), then by address.
    /// </summary>
    public async Task<PagedDto<ReviewSenderDto>> ListAsync(
        SuggestionStatus status, string? search, int page, int pageSize, CancellationToken ct)
    {
        var suggestions = db.Suggestions.AsNoTracking();
        if (search is not null)
        {
            var matching = SenderQuery.Filter(db.Senders.AsNoTracking(), search).Select(x => x.Address);
            suggestions = suggestions.Where(s => matching.Contains(s.SenderAddress));
        }

        var counts = suggestions
            .GroupBy(s => s.SenderAddress)
            .Select(g => new StatusCounts
            {
                Address = g.Key,
                Pending = g.Count(s => s.Status == SuggestionStatus.Pending),
                Approved = g.Count(s => s.Status == SuggestionStatus.Approved),
                Rejected = g.Count(s => s.Status == SuggestionStatus.Rejected),
                Applied = g.Count(s => s.Status == SuggestionStatus.Applied),
            });
        var ordered = status switch
        {
            SuggestionStatus.Approved => counts.Where(c => c.Approved > 0).OrderByDescending(c => c.Approved),
            SuggestionStatus.Rejected => counts.Where(c => c.Rejected > 0).OrderByDescending(c => c.Rejected),
            _ => counts.Where(c => c.Pending > 0).OrderByDescending(c => c.Pending),
        };

        var total = await ordered.LongCountAsync(ct);
        var rows = await ordered.ThenBy(c => c.Address).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var addresses = rows.ConvertAll(r => r.Address);
        var senders = await db.Senders.AsNoTracking()
            .Where(s => addresses.Contains(s.Address))
            .ToDictionaryAsync(s => s.Address, ct);
        return new PagedDto<ReviewSenderDto>([.. rows.Select(r => ToDto(r, senders.GetValueOrDefault(r.Address)))], page, pageSize, total);
    }

    /// <summary>
    /// One page of the sender's suggestions in <paramref name="status"/>, grouped (a message analysed alone is its own
    /// group), largest group first; null when the sender has no suggestion at all. Groups are counted and paged in SQL;
    /// members are loaded per group, newest first, within <see cref="MaxMembers"/> and <see cref="MaxResponseMembers"/>.
    /// </summary>
    public async Task<ReviewSenderDetailDto?> DetailAsync(string address, SuggestionStatus status, int page, int pageSize, CancellationToken ct)
    {
        var counts = await db.Suggestions.AsNoTracking()
            .Where(s => s.SenderAddress == address)
            .GroupBy(s => s.SenderAddress)
            .Select(g => new StatusCounts
            {
                Address = g.Key,
                Pending = g.Count(s => s.Status == SuggestionStatus.Pending),
                Approved = g.Count(s => s.Status == SuggestionStatus.Approved),
                Rejected = g.Count(s => s.Status == SuggestionStatus.Rejected),
                Applied = g.Count(s => s.Status == SuggestionStatus.Applied),
            })
            .SingleOrDefaultAsync(ct);
        if (counts is null)
        {
            return null;
        }

        var sender = await db.Senders.AsNoTracking().SingleOrDefaultAsync(s => s.Address == address, ct);
        var allowlisted = sender?.Allowlisted ?? false;
        var inStatus = db.Suggestions.AsNoTracking().Where(s => s.SenderAddress == address && s.Status == status);
        var stats = inStatus
            .GroupBy(s => s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId)
            .Select(g => new GroupStats
            {
                Key = g.Key,
                Size = g.Count(),
                Llm = g.Count(s => s.Source == SuggestionSource.Llm),
                Derived = g.Count(s => s.Source == SuggestionSource.Derived),
                Memory = g.Count(s => s.Source == SuggestionSource.Memory),
                ConfidenceMin = g.Min(s => s.Confidence),
                ConfidenceMax = g.Max(s => s.Confidence),
            });
        var totalGroups = await stats.LongCountAsync(ct);
        var pageStats = await stats.OrderByDescending(g => g.Size).ThenBy(g => g.Key)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var keys = pageStats.ConvertAll(g => g.Key);
        var outcomes = (await inStatus
                .Where(s => keys.Contains(s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId))
                .GroupBy(s => new { Key = s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId, s.TopicLabel, s.NeedsAction, s.ToBeDeleted })
                .Select(g => new OutcomeCount(
                    g.Key.Key, g.Key.TopicLabel, g.Key.NeedsAction, g.Key.ToBeDeleted, g.Count(), g.Count(s => s.Source == SuggestionSource.Llm) > 0))
                .ToListAsync(ct))
            .ToLookup(o => o.Key, StringComparer.Ordinal);

        var groups = new List<ReviewGroupDto>(pageStats.Count);
        var budget = MaxResponseMembers;
        foreach (var g in pageStats)
        {
            var take = Math.Clamp(budget, 1, MaxMembers);
            var key = g.Key;
            var members = await (
                    from s in inStatus
                    join m in db.Messages.AsNoTracking() on s.MessageId equals m.Id
                    where (s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId) == key
                    orderby m.InternalDate descending, m.Id
                    select new { Suggestion = s, Message = m })
                .Take(take)
                .ToListAsync(ct);
            budget -= members.Count;
            groups.Add(ToGroup(g, outcomes[key], [.. members.Select(r => (r.Suggestion, r.Message))], allowlisted));
        }

        return new ReviewSenderDetailDto(ToDto(counts, sender), groups, page, pageSize, totalGroups);
    }

    public static SuggestionDto ToDto(SuggestionRow s, MessageRow m, bool senderAllowlisted) => new(
        s.Id,
        s.MessageId,
        m.Subject,
        m.InternalDate,
        m.Snippet,
        SnakeCaseEnumConverter<SuggestionSource>.ToDb(s.Source),
        s.TopicLabel,
        s.IsNewLabel,
        s.NeedsAction,
        s.ToBeDeleted,
        s.UnsubscribeSuggested,
        s.Confidence,
        s.Reason,
        SnakeCaseEnumConverter<SuggestionStatus>.ToDb(s.Status),
        s.Edited,
        MessageProtection.IsProtected(m, senderAllowlisted));

    /// <summary>
    /// The group shows its most common outcome (over all members), with the reason of a listed model-analysed member
    /// that has it when there is one; <c>Mixed</c> when members disagree. Members are newest first.
    /// </summary>
    private static ReviewGroupDto ToGroup(
        GroupStats stats, IEnumerable<OutcomeCount> outcomes, IReadOnlyList<(SuggestionRow S, MessageRow M)> members, bool allowlisted)
    {
        var all = outcomes.ToList();
        var shared = all
            .OrderByDescending(o => o.Count)
            .ThenByDescending(o => o.HasLlm)
            .ThenBy(o => o.TopicLabel, StringComparer.Ordinal)
            .First();
        bool Matches(SuggestionRow s) =>
            s.TopicLabel == shared.TopicLabel && s.NeedsAction == shared.NeedsAction && s.ToBeDeleted == shared.ToBeDeleted;
        var representative = members.FirstOrDefault(x => Matches(x.S) && x.S.Source == SuggestionSource.Llm);
        if (representative.S is null)
        {
            representative = members.FirstOrDefault(x => Matches(x.S));
        }

        if (representative.S is null)
        {
            representative = members[0];
        }

        var newest = members[0];
        var key = newest.S.GroupKey;
        var display = (string.IsNullOrWhiteSpace(newest.M.Subject) ? AnalysisGrouper.NoSubjectDisplay : newest.M.Subject)
            + (key is not null && GroupKey.IsList(key) ? AnalysisGrouper.ListDisplaySuffix : "");
        return new ReviewGroupDto(
            key,
            display,
            stats.Size,
            stats.Llm,
            stats.Derived,
            stats.Memory,
            shared.TopicLabel,
            shared.NeedsAction,
            shared.ToBeDeleted,
            all.Count > 1,
            stats.ConfidenceMin,
            stats.ConfidenceMax,
            representative.S.Reason,
            [.. members.Select(x => ToDto(x.S, x.M, allowlisted))],
            members.Count < stats.Size);
    }

    private static ReviewSenderDto ToDto(StatusCounts c, SenderRow? sender) => new(
        c.Address, sender?.DisplayName, c.Pending, c.Approved, c.Rejected, c.Applied, sender?.TotalCount ?? 0);

    /// <summary>A class with settable members (not a record) so EF can filter and sort on the projection.</summary>
    private sealed class StatusCounts
    {
        public string Address { get; init; } = "";
        public int Pending { get; init; }
        public int Approved { get; init; }
        public int Rejected { get; init; }
        public int Applied { get; init; }
    }

    /// <summary>A class with settable members so EF can sort and page on the projection.</summary>
    private sealed class GroupStats
    {
        public string Key { get; init; } = "";
        public int Size { get; init; }
        public int Llm { get; init; }
        public int Derived { get; init; }
        public int Memory { get; init; }
        public double ConfidenceMin { get; init; }
        public double ConfidenceMax { get; init; }
    }

    private sealed record OutcomeCount(string Key, string TopicLabel, bool NeedsAction, bool ToBeDeleted, int Count, bool HasLlm);
}
