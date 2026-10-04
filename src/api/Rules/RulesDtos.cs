using GmailOrganiser.Review;

namespace GmailOrganiser.Rules;

/// <summary>The outcome of one filter sync.</summary>
/// <param name="Total">Filters Gmail listed.</param>
/// <param name="Added">Filters new to the snapshot, or listed again after being marked deleted.</param>
/// <param name="Removed">Filters newly marked deleted because Gmail no longer lists them.</param>
public sealed record FilterSyncResultDto(int Total, int Added, int Removed, DateTimeOffset SyncedAt);

/// <param name="SyncedAt">The last sync; null before the first one.</param>
/// <param name="ActiveCount">Filters not marked deleted.</param>
/// <param name="Limit">Gmail's per-account filter limit.</param>
public sealed record FilterListDto(DateTimeOffset? SyncedAt, int ActiveCount, int Limit, IReadOnlyList<FilterDto> Filters);

public sealed record FilterDto(
    string Id,
    FilterCriteriaDto Criteria,
    string CriteriaSummary,
    FilterActionDto Action,
    bool CreatedByApp,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset? DeletedAt,
    bool DeletedByApp,
    string? RestoredFrom);

/// <param name="SizeComparison"><c>smaller</c> or <c>larger</c>.</param>
public sealed record FilterCriteriaDto(
    string? From,
    string? To,
    string? Subject,
    string? Query,
    string? NegatedQuery,
    bool? HasAttachment,
    bool? ExcludeChats,
    int? Size,
    string? SizeComparison);

/// <param name="SkipInbox">The filter removes <c>INBOX</c>.</param>
/// <param name="MarkRead">The filter removes <c>UNREAD</c>.</param>
/// <param name="Forwards">The filter forwards mail; the address is not shown.</param>
public sealed record FilterActionDto(
    IReadOnlyList<LabelRefDto> AddLabels, IReadOnlyList<string> RemoveLabelIds, bool SkipInbox, bool MarkRead, bool Forwards);

/// <param name="Name">Null when the mailbox has no label with this id (for example, it was deleted).</param>
public sealed record LabelRefDto(string Id, string? Name);

/// <param name="AddLabelNames">Label paths; missing ones are created on create.</param>
/// <param name="SkipInbox">Remove <c>INBOX</c>.</param>
/// <param name="MarkRead">Remove <c>UNREAD</c>.</param>
public sealed record FilterActionRequest(IReadOnlyList<string>? AddLabelNames, bool SkipInbox, bool MarkRead);

public sealed record FilterPreviewRequest(FilterCriteriaDto? Criteria, FilterActionRequest? Action);

public sealed record CreateFilterRequest(FilterCriteriaDto? Criteria, FilterActionRequest? Action);

/// <param name="Query">The Gmail search equivalent of the criteria.</param>
/// <param name="LocalMatches">Stored messages the criteria match (Spam and Trash are never stored); null when a criterion has no local equivalent.</param>
/// <param name="GmailEstimate">Gmail's result size estimate for <paramref name="Query"/>; null when Gmail could not answer.</param>
/// <param name="Action">Labels that don't exist yet have an empty id and are listed in <paramref name="CreatesLabels"/>.</param>
/// <param name="CreatesLabels">Label paths (parents included) a create would add to the mailbox.</param>
public sealed record FilterPreviewDto(
    FilterCriteriaDto Criteria,
    string CriteriaSummary,
    string Query,
    int? LocalMatches,
    long? GmailEstimate,
    FilterActionDto Action,
    IReadOnlyList<string> CreatesLabels,
    IReadOnlyList<string> Warnings);

public sealed record FilterSuggestionDto(FilterCriteriaDto Criteria, FilterActionRequest Action);

/// <param name="ListId">The List-Id all the sender's stored messages share, if any.</param>
public sealed record FilterProposalDto(
    string SenderAddress, string? DisplayName, int MessageCount, string? ListId, SenderPatternDto Pattern, FilterSuggestionDto Suggested);
