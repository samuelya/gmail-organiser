namespace GmailOrganiser.Claude;

/// <summary>A Claude review item; enums are snake_case. <c>GroupDisplay</c> is the group card's title, computed on read.</summary>
public sealed record ExternalReviewDto(
    Guid Id,
    string TargetType,
    Guid? SuggestionId,
    string SenderAddress,
    string? GroupKey,
    string? GroupDisplay,
    string Status,
    string? Reviewer,
    string? Verdict,
    string? VerdictTopicLabel,
    bool? VerdictNeedsAction,
    bool? VerdictToBeDeleted,
    string? VerdictDocumentTypeLabel,
    string? Reasoning,
    string? Error,
    string Resolution,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? ResolvedAt);

/// <summary>At least one of the three; <c>RunId</c> expands to the run's groups with pending members.</summary>
public sealed record CreateExternalReviewsRequest(Guid[]? SuggestionIds, GroupRef[]? Groups, Guid? RunId);

public sealed record GroupRef(string SenderAddress, string GroupKey);

public sealed record CreateExternalReviewsResponse(int Created, int Skipped, IReadOnlyList<ExternalReviewDto> Items);

public sealed record ExternalReviewSummaryDto(int Queued, int Running, int Reviewed, int Unavailable);

/// <summary>Claude's verdict as the MCP <c>submit_review</c> tool passes it.</summary>
/// <param name="DocumentTypeLabel">For <c>alternative</c>: null keeps the type Claude was shown, blank clears it.</param>
public sealed record ReviewVerdictInput(
    ReviewVerdict Verdict,
    string? TopicLabel,
    bool? NeedsAction,
    bool? ToBeDeleted,
    string? FilterCriteria,
    string Reasoning,
    string Reviewer,
    string? Model,
    string? DocumentTypeLabel = null);
