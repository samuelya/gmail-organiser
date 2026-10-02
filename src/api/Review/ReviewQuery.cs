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
    public const int MaxMembers = 500;

    private const char LikeEscape = '\\';

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
            var pattern = $"%{SenderQuery.EscapeLike(search)}%";
            suggestions = suggestions.Where(s =>
                EF.Functions.ILike(s.SenderAddress, pattern, LikeEscape.ToString())
                || db.Senders.Any(x => x.Address == s.SenderAddress
                    && (EF.Functions.ILike(x.Domain, pattern, LikeEscape.ToString())
                        || (x.DisplayName != null && EF.Functions.ILike(x.DisplayName, pattern, LikeEscape.ToString())))));
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
    /// The sender's suggestions in <paramref name="status"/>, grouped (a message analysed alone is its own group),
    /// largest group first; null when the sender has no suggestion at all.
    /// </summary>
    public async Task<ReviewSenderDetailDto?> DetailAsync(string address, SuggestionStatus status, CancellationToken ct)
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
        var rows = await (
                from s in db.Suggestions.AsNoTracking()
                join m in db.Messages.AsNoTracking() on s.MessageId equals m.Id
                where s.SenderAddress == address && s.Status == status
                orderby m.InternalDate descending, m.Id
                select new { Suggestion = s, Message = m })
            .ToListAsync(ct);
        var allowlisted = sender?.Allowlisted ?? false;
        var groups = rows
            .GroupBy(r => r.Suggestion.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + r.Suggestion.MessageId, StringComparer.Ordinal)
            .Select(g => ToGroup([.. g.Select(r => (r.Suggestion, r.Message))], allowlisted))
            .OrderByDescending(g => g.Size)
            .ThenBy(g => g.Display, StringComparer.Ordinal)
            .ToList();
        return new ReviewSenderDetailDto(ToDto(counts, sender), groups);
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
    /// The group shows its most common outcome, with the reason of a model-analysed member that has it when there is
    /// one; <c>Mixed</c> when members disagree. Members are newest first.
    /// </summary>
    private static ReviewGroupDto ToGroup(IReadOnlyList<(SuggestionRow S, MessageRow M)> members, bool allowlisted)
    {
        var shared = members
            .GroupBy(x => (x.S.TopicLabel, x.S.NeedsAction, x.S.ToBeDeleted))
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Any(x => x.S.Source == SuggestionSource.Llm))
            .First();
        var representative = shared.FirstOrDefault(x => x.S.Source == SuggestionSource.Llm);
        if (representative.S is null)
        {
            representative = shared.First();
        }

        var newest = members[0];
        var key = newest.S.GroupKey;
        var display = (string.IsNullOrWhiteSpace(newest.M.Subject) ? AnalysisGrouper.NoSubjectDisplay : newest.M.Subject)
            + (key is not null && GroupKey.IsList(key) ? AnalysisGrouper.ListDisplaySuffix : "");
        return new ReviewGroupDto(
            key,
            display,
            members.Count,
            members.Count(x => x.S.Source == SuggestionSource.Llm),
            members.Count(x => x.S.Source == SuggestionSource.Derived),
            members.Count(x => x.S.Source == SuggestionSource.Memory),
            shared.Key.TopicLabel,
            shared.Key.NeedsAction,
            shared.Key.ToBeDeleted,
            shared.Count() != members.Count,
            members.Min(x => x.S.Confidence),
            members.Max(x => x.S.Confidence),
            representative.S.Reason,
            [.. members.Take(MaxMembers).Select(x => ToDto(x.S, x.M, allowlisted))],
            members.Count > MaxMembers);
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
}
