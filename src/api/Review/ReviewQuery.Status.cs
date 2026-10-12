using GmailOrganiser.Analysis;

namespace GmailOrganiser.Review;

/// <summary>The review read paths' status filter (#536): one status, or every status but rejected.</summary>
public sealed partial class ReviewQuery
{
    /// <summary>The validation message for a status <see cref="ParseStatusFilter"/> refuses.</summary>
    public const string StatusFilterError = "Must be pending, approved, rejected, applied or not_rejected.";

    private static readonly IReadOnlyList<SuggestionStatus> NotRejected =
        [SuggestionStatus.Pending, SuggestionStatus.Approved, SuggestionStatus.Applied];

    /// <summary>
    /// Parses <c>pending|approved|rejected|applied|not_rejected</c> (default pending), trimmed and case-insensitive, into
    /// the statuses to list; <c>not_rejected</c> is pending, approved and applied, what the Dashboard mail-type bars
    /// count. Null when invalid. For the senders list and detail only; everything else takes <see cref="ParseStatus"/>.
    /// </summary>
    public static IReadOnlyList<SuggestionStatus>? ParseStatusFilter(string? value) =>
        value?.Trim().ToLowerInvariant() == "not_rejected" ? NotRejected : ParseStatus(value) is { } s ? [s] : null;

    /// <summary>
    /// Per sender, the suggestions per status and <see cref="StatusCounts.InFilter"/>, those in <paramref name="statuses"/>.
    /// </summary>
    private static IQueryable<StatusCounts> CountsBySender(IQueryable<SuggestionRow> suggestions, IReadOnlyList<SuggestionStatus> statuses) =>
        suggestions
            .GroupBy(s => s.SenderAddress)
            .Select(g => new StatusCounts
            {
                Address = g.Key,
                Pending = g.Count(s => s.Status == SuggestionStatus.Pending),
                Approved = g.Count(s => s.Status == SuggestionStatus.Approved),
                Rejected = g.Count(s => s.Status == SuggestionStatus.Rejected),
                Applied = g.Count(s => s.Status == SuggestionStatus.Applied),
                InFilter = g.Count(s => statuses.Contains(s.Status)),
            });
}
