namespace GmailOrganiser.Claude;

/// <summary>A Claude review item; enums are snake_case. <c>GroupDisplay</c> is the group card's title, computed on read.</summary>
/// <param name="VerdictDocumentTypeSet">False: accepting keeps each member's document type; <c>VerdictDocumentTypeLabel</c> is null.</param>
/// <param name="AlternativeStructure">A label plan's <c>alternative</c>: the label paths Claude proposes.</param>
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
    bool VerdictDocumentTypeSet,
    string? Reasoning,
    string? Error,
    string Resolution,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? ResolvedAt,
    Guid? LabelPlanId = null,
    Guid? FindingId = null,
    IReadOnlyList<string>? AlternativeStructure = null);

/// <summary>
/// At least one field; <c>RunId</c> expands to the run's groups with pending members. <c>LabelPlanId</c> must be a
/// <c>draft</c> plan and <c>FindingIds</c> <c>open</c> findings, each without an open item.
/// </summary>
public sealed record CreateExternalReviewsRequest(
    Guid[]? SuggestionIds, GroupRef[]? Groups, Guid? RunId, Guid? LabelPlanId = null, Guid[]? FindingIds = null);

/// <param name="Status">
/// The review list the group card was shown in (<c>pending|approved|rejected</c>, pending when omitted); alternative
/// accept/discard act only on members in it. Claude reviews always take a group's pending members.
/// </param>
public sealed record GroupRef(string SenderAddress, string GroupKey, string? Status = null);

public sealed record CreateExternalReviewsResponse(int Created, int Skipped, IReadOnlyList<ExternalReviewDto> Items);

public sealed record ExternalReviewSummaryDto(int Queued, int Running, int Reviewed, int Unavailable);

/// <summary>Claude's verdict as the MCP <c>submit_review</c> tool passes it.</summary>
/// <param name="DocumentTypeLabel">For <c>alternative</c> only: null keeps each member's type, blank clears it.</param>
/// <param name="AlternativeStructure">For a label plan's <c>alternative</c> only: the proposed label paths.</param>
public sealed record ReviewVerdictInput(
    ReviewVerdict Verdict,
    string? TopicLabel,
    bool? NeedsAction,
    bool? ToBeDeleted,
    string? FilterCriteria,
    string Reasoning,
    string Reviewer,
    string? Model,
    string? DocumentTypeLabel = null,
    IReadOnlyList<string>? AlternativeStructure = null);
