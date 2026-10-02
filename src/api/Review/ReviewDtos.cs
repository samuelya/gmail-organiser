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

/// <param name="SenderAddress">Only this sender's approved suggestions.</param>
/// <param name="SuggestionIds">Only these suggestions (those still approved); null for every approved one.</param>
public sealed record ApplyRequest(string? SenderAddress = null, Guid[]? SuggestionIds = null);

public sealed record ActionBatchDto(
    Guid Id,
    string Kind,
    string Description,
    int MessageCount,
    Guid? UndoOf,
    DateTimeOffset? UndoneAt,
    Guid? JobId,
    DateTimeOffset CreatedAt,
    bool CanUndo)
{
    public static ActionBatchDto From(ActionBatchRow b) => new(
        b.Id,
        Data.SnakeCaseEnumConverter<ActionKind>.ToDb(b.Kind),
        b.Description,
        b.MessageCount,
        b.UndoOf,
        b.UndoneAt,
        b.JobId,
        b.CreatedAt,
        b.Kind != ActionKind.Undo && b.UndoneAt is null && b.MessageCount > 0);
}

/// <summary>The sender's most approved outcome; the outcome fields are null when nothing is approved yet.</summary>
/// <param name="Approvals">Approved or applied suggestions the user decided (pattern suggestions excluded).</param>
/// <param name="Agreement">Share of <paramref name="Approvals"/> with this outcome, 0–1.</param>
/// <param name="Remaining">Messages without a suggestion, not deleted in Gmail.</param>
public sealed record SenderPatternDto(
    string? TopicLabel, bool? NeedsAction, bool? ToBeDeleted, int Approvals, double Agreement, int Remaining);

/// <summary>Each value overrides the pattern's; without a pattern the topic label is required and the flags default to false.</summary>
public sealed record ApplyRestRequest(string? TopicLabel = null, bool? NeedsAction = null, bool? ToBeDeleted = null);

/// <summary>The criteria of the Gmail filter that matches the sender; the input of filter creation (M6).</summary>
/// <param name="ListId">The List-Id all the sender's messages share, if any.</param>
public sealed record FilterCandidateDto(string From, string? ListId);

/// <param name="ProtectedAdjusted">Created without to-be-deleted because the message is protected.</param>
/// <param name="Batch">The queued apply batch; null when nothing remained.</param>
public sealed record ApplyRestResponse(int Created, int ProtectedAdjusted, ActionBatchDto? Batch, FilterCandidateDto FilterCandidate);
