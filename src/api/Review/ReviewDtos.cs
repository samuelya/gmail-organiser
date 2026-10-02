namespace GmailOrganiser.Review;

public sealed record ReviewSenderDto(
    string Address, string? DisplayName, int Pending, int Approved, int Rejected, int Applied, int TotalMessages);

/// <param name="Groups">One page of the sender's groups, largest first.</param>
/// <param name="TotalGroups">All the sender's groups in the requested status.</param>
public sealed record ReviewSenderDetailDto(
    ReviewSenderDto Sender, IReadOnlyList<ReviewGroupDto> Groups, int Page, int PageSize, long TotalGroups);

/// <param name="GroupKey">Null for a message analysed on its own.</param>
/// <param name="Mixed">Members disagree on label, needs-action or to-be-deleted (after edits).</param>
/// <param name="Truncated">Not every member is listed (<see cref="ReviewQuery.MaxMembers"/>, <see cref="ReviewQuery.MaxResponseMembers"/>); the aggregates count them all.</param>
public sealed record ReviewGroupDto(
    string? GroupKey,
    string Display,
    int Size,
    int LlmCount,
    int DerivedCount,
    int MemoryCount,
    string TopicLabel,
    bool NeedsAction,
    bool ToBeDeleted,
    bool Mixed,
    double ConfidenceMin,
    double ConfidenceMax,
    string Reason,
    IReadOnlyList<SuggestionDto> Members,
    bool Truncated);

public sealed record SuggestionDto(
    Guid Id,
    string MessageId,
    string? Subject,
    DateTimeOffset Date,
    string? Snippet,
    string Source,
    string TopicLabel,
    bool IsNewLabel,
    bool NeedsAction,
    bool ToBeDeleted,
    bool UnsubscribeSuggested,
    double Confidence,
    string Reason,
    string Status,
    bool Edited,
    bool Protected);

public sealed record LabelDto(string Id, string Name, string Type);

/// <summary>Every field is required.</summary>
public sealed record EditSuggestionRequest(string? TopicLabel, bool? NeedsAction, bool? ToBeDeleted);

/// <param name="TopicLabel">Approve only: the outcome the card shows; only members with exactly this outcome are approved.</param>
public sealed record GroupDecisionRequest(
    string? SenderAddress, string? GroupKey, string? TopicLabel = null, bool? NeedsAction = null, bool? ToBeDeleted = null);

/// <param name="Skipped">Pending members left pending (another outcome than the card's, or a protected deletion), at most <see cref="ReviewService.MaxSkippedIds"/>.</param>
public sealed record GroupDecisionResponse(int Changed, IReadOnlyList<Guid> Skipped);

/// <param name="Threshold">Defaults to the <c>BulkApproveThreshold</c> setting.</param>
/// <param name="IncludeDerived">Also approve derived and memory suggestions.</param>
public sealed record BulkApproveRequest(double? Threshold, bool IncludeDerived = false, string? SenderAddress = null);

/// <param name="SkippedProtected">To-be-deleted suggestions left pending because their message is protected.</param>
/// <param name="SkippedIds">Their ids, at most <see cref="ReviewService.MaxSkippedIds"/>.</param>
public sealed record BulkApproveResponse(int Approved, int SkippedProtected, IReadOnlyList<Guid> SkippedIds);

public sealed record AnalyseIndividuallyRequest(Guid[]? SuggestionIds);
