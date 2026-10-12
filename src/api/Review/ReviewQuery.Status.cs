using GmailOrganiser.Analysis;

namespace GmailOrganiser.Review;

/// <summary>The review read paths' status filter (#536): one status, or every status but rejected.</summary>
public sealed partial class ReviewQuery
{
    private static readonly IReadOnlyList<SuggestionStatus> NotRejected =
        [SuggestionStatus.Pending, SuggestionStatus.Approved, SuggestionStatus.Applied];

    /// <summary>
    /// Parses <c>pending|approved|rejected|applied|not_rejected</c> (default pending), trimmed and case-insensitive, into
    /// the statuses to list; <c>not_rejected</c> is pending, approved and applied, what the Dashboard mail-type bars
    /// count. Null when invalid. For the senders list and detail only; everything else takes <see cref="ParseStatus"/>.
    /// </summary>
    public static IReadOnlyList<SuggestionStatus>? ParseStatusFilter(string? value) =>
        value?.Trim().ToLowerInvariant() == "not_rejected" ? NotRejected : ParseStatus(value) is { } s ? [s] : null;

    /// <summary>Senders with at least one suggestion in <paramref name="statuses"/>, the most of them first.</summary>
    private static IOrderedQueryable<StatusCounts> InStatuses(IQueryable<StatusCounts> counts, IReadOnlyList<SuggestionStatus> statuses) =>
        statuses switch
        {
            [SuggestionStatus.Approved] => counts.Where(c => c.Approved > 0).OrderByDescending(c => c.Approved),
            [SuggestionStatus.Rejected] => counts.Where(c => c.Rejected > 0).OrderByDescending(c => c.Rejected),
            [SuggestionStatus.Applied] => counts.Where(c => c.Applied > 0).OrderByDescending(c => c.Applied),
            [SuggestionStatus.Pending] => counts.Where(c => c.Pending > 0).OrderByDescending(c => c.Pending),
            _ => counts.Where(c => c.Pending + c.Approved + c.Applied > 0).OrderByDescending(c => c.Pending + c.Approved + c.Applied),
        };

    /// <summary>
    /// Whether a multi-status filter (<c>not_rejected</c>) matches none of the sender's suggestions; a single status keeps
    /// listing the sender with no groups, as before.
    /// </summary>
    private static bool IsEmptyFilter(StatusCounts c, IReadOnlyList<SuggestionStatus> statuses) =>
        statuses.Count > 1 && statuses.Sum(s => s switch
        {
            SuggestionStatus.Pending => c.Pending,
            SuggestionStatus.Approved => c.Approved,
            SuggestionStatus.Rejected => c.Rejected,
            _ => c.Applied,
        }) == 0;
}
