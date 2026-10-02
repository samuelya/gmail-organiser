namespace GmailOrganiser.Review;

public sealed record ReviewSenderDto(
    string Address, string? DisplayName, int Pending, int Approved, int Rejected, int Applied, int TotalMessages);

public sealed record ReviewSenderDetailDto(ReviewSenderDto Sender, IReadOnlyList<ReviewGroupDto> Groups);

/// <param name="GroupKey">Null for a message analysed on its own.</param>
/// <param name="Mixed">Members disagree on label, needs-action or to-be-deleted (after edits).</param>
/// <param name="Truncated">The group has more than <see cref="ReviewQuery.MaxMembers"/> members; the aggregates count them all.</param>
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

public sealed record EditSuggestionRequest(string? TopicLabel, bool NeedsAction, bool ToBeDeleted);

public sealed record GroupDecisionRequest(string? SenderAddress, string? GroupKey);

public sealed record GroupDecisionResponse(int Changed);

/// <param name="Threshold">Defaults to the <c>BulkApproveThreshold</c> setting.</param>
/// <param name="IncludeDerived">Also approve derived and memory suggestions.</param>
public sealed record BulkApproveRequest(double? Threshold, bool IncludeDerived = false, string? SenderAddress = null);

/// <param name="SkippedProtected">To-be-deleted suggestions left pending because their message is protected.</param>
public sealed record BulkApproveResponse(int Approved, int SkippedProtected);

public sealed record AnalyseIndividuallyRequest(Guid[]? SuggestionIds);
