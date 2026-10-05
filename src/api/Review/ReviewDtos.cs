using GmailOrganiser.Claude;

namespace GmailOrganiser.Review;

/// <param name="PolicyId">The approved policy covering the sender (<see cref="Policies.PolicyLookup.ForSendersAsync"/>); null when none.</param>
public sealed record ReviewSenderDto(
    string Address, string? DisplayName, int Pending, int Approved, int Rejected, int Applied, int TotalMessages, Guid? PolicyId);

/// <param name="Groups">One page of the sender's groups, largest first.</param>
/// <param name="TotalGroups">All the sender's groups in the requested status.</param>
public sealed record ReviewSenderDetailDto(
    ReviewSenderDto Sender, IReadOnlyList<ReviewGroupDto> Groups, int Page, int PageSize, long TotalGroups);

/// <param name="GroupKey">Null for a message analysed on its own.</param>
/// <param name="Mixed">Members disagree on label, needs-action, to-be-deleted or document type (after edits).</param>
/// <param name="Truncated">Not every member is listed (<see cref="ReviewQuery.MaxMembers"/>, <see cref="ReviewQuery.MaxResponseMembers"/>); the aggregates count them all.</param>
/// <param name="ReplaceLabels">Union of the listed members' replaced labels, distinct and ordinal-sorted.</param>
/// <param name="LabelChange">The listed members' label change when they agree; else relabel when any member replaces a label, add otherwise.</param>
/// <param name="ClaudeReview">The newest not-cancelled Claude review item for the group.</param>
/// <param name="SuggestedForClaude">Any member, listed or not, is worth a Claude review (<see cref="ReviewQuery.IsSuggestedForClaude"/>).</param>
/// <param name="DocumentTypeLabel">The shown outcome's document-type label; null when it has none.</param>
/// <param name="DocumentTypeIsNew">A listed member with the shown document-type label would create it in Gmail.</param>
/// <param name="Alternative">The listed members' compare-run alternative (<see cref="SuggestionAlternativeDto.Mixed"/> when they disagree); null when none has one.</param>
public sealed record ReviewGroupDto(
    string? GroupKey,
    string Display,
    int Size,
    int LlmCount,
    int DerivedCount,
    int MemoryCount,
    int Stage0Count,
    string TopicLabel,
    bool NeedsAction,
    bool ToBeDeleted,
    bool Mixed,
    double ConfidenceMin,
    double ConfidenceMax,
    string Reason,
    IReadOnlyList<SuggestionDto> Members,
    bool Truncated,
    IReadOnlyList<string> ReplaceLabels,
    LabelChange LabelChange,
    ExternalReviewDto? ClaudeReview = null,
    bool SuggestedForClaude = false,
    string? DocumentTypeLabel = null,
    bool DocumentTypeIsNew = false,
    SuggestionAlternativeDto? Alternative = null);

/// <param name="ReplaceLabels">Current labels apply removes: the replaced labels the message still carries, never the topic label.</param>
/// <param name="CurrentLabels">The message's personal (user) label names; empty when Gmail is not reachable.</param>
/// <param name="ClaudeReview">The newest not-cancelled Claude review item for this suggestion alone (not its group's).</param>
/// <param name="SuggestedForClaude">Worth a Claude review by the settings (<see cref="ReviewQuery.IsSuggestedForClaude"/>); a UI hint only.</param>
/// <param name="DocumentTypeLabel">The second label apply adds next to the topic label; null when none.</param>
/// <param name="DocumentTypeIsNew">Gmail did not have <paramref name="DocumentTypeLabel"/> when it was suggested or edited.</param>
/// <param name="Alternative">The compare run's answer stored next to it (#248); null when none.</param>
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
    bool Protected,
    IReadOnlyList<string> ReplaceLabels,
    IReadOnlyList<string> CurrentLabels,
    LabelChange LabelChange,
    ExternalReviewDto? ClaudeReview = null,
    bool SuggestedForClaude = false,
    string? DocumentTypeLabel = null,
    bool DocumentTypeIsNew = false,
    SuggestionAlternativeDto? Alternative = null);

/// <summary>A compare run's alternative to a suggestion, shown next to it; nothing changes until it is accepted.</summary>
/// <param name="ReplaceLabels">As on <see cref="SuggestionDto.ReplaceLabels"/>, for the alternative's topic label.</param>
/// <param name="Mixed">Group only: the listed members' alternatives disagree; the fields show the most common one.</param>
/// <param name="Count">Listed members with an alternative (1 for a suggestion).</param>
public sealed record SuggestionAlternativeDto(
    string TopicLabel,
    string? DocumentTypeLabel,
    IReadOnlyList<string> ReplaceLabels,
    LabelChange LabelChange,
    bool NeedsAction,
    bool ToBeDeleted,
    bool UnsubscribeSuggested,
    double Confidence,
    string Reason,
    string? PromptVersion,
    string? Model,
    DateTimeOffset CreatedAt,
    bool Mixed = false,
    int Count = 1);

/// <summary>Suggestions by id and every suggestion of the named groups (any status); at least one of the two.</summary>
public sealed record AlternativeDecisionRequest(Guid[]? SuggestionIds, GroupRef[]? Groups);

/// <param name="Skipped">Suggestions with an alternative left alone: part of an active apply batch.</param>
public sealed record AlternativeDecisionResponse(int Accepted, int Discarded, int Skipped);

public sealed record LabelDto(string Id, string Name, string Type);

/// <summary>Every field but <paramref name="ReplaceLabels"/> and <paramref name="DocumentTypeLabel"/> is required.</summary>
/// <param name="ReplaceLabels">Current labels of the message to replace; null leaves them unchanged.</param>
/// <param name="DocumentTypeLabel">Null leaves it unchanged, <c>""</c> clears it (<see cref="DocumentTypeEdit"/>).</param>
public sealed record EditSuggestionRequest(
    string? TopicLabel, bool? NeedsAction, bool? ToBeDeleted, string[]? ReplaceLabels = null, string? DocumentTypeLabel = null);

/// <param name="TopicLabel">Approve only: the outcome the card shows; only members with exactly this outcome are approved.</param>
/// <param name="ReplaceLabels">Approve only, optional: each approved member keeps only its own replaced labels named here; none are added.</param>
/// <param name="DocumentTypeLabel">Approve only: the card's document-type label; absent or blank approves only members without one.</param>
public sealed record GroupDecisionRequest(
    string? SenderAddress,
    string? GroupKey,
    string? TopicLabel = null,
    bool? NeedsAction = null,
    bool? ToBeDeleted = null,
    string[]? ReplaceLabels = null,
    string? DocumentTypeLabel = null);

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
/// <param name="DocumentTypeLabel">
/// The most common document-type label among the outcome's approvals; outcomes are by topic and flags only, so the type
/// never changes the top outcome or <paramref name="Agreement"/> (filter proposals use them).
/// </param>
public sealed record SenderPatternDto(
    string? TopicLabel, bool? NeedsAction, bool? ToBeDeleted, int Approvals, double Agreement, int Remaining,
    string? DocumentTypeLabel = null);

/// <summary>Each value overrides the pattern's; without a pattern the topic label is required and the flags default to false.</summary>
/// <param name="DocumentTypeLabel">Null keeps the pattern's, <c>""</c> sets none (<see cref="DocumentTypeEdit"/>).</param>
public sealed record ApplyRestRequest(
    string? TopicLabel = null, bool? NeedsAction = null, bool? ToBeDeleted = null, string? DocumentTypeLabel = null);

/// <summary>The criteria of the Gmail filter that matches the sender; the input of filter creation (M6).</summary>
/// <param name="ListId">The List-Id all the sender's messages share, if any.</param>
public sealed record FilterCandidateDto(string From, string? ListId);

/// <param name="ProtectedAdjusted">Created without to-be-deleted because the message is protected.</param>
/// <param name="Batch">The queued apply batch; null when nothing remained.</param>
public sealed record ApplyRestResponse(int Created, int ProtectedAdjusted, ActionBatchDto? Batch, FilterCandidateDto FilterCandidate);

/// <param name="LabelsAdded">Gmail label ids the action added; <paramref name="LabelNamesAdded"/> are their current names (the id when unknown).</param>
/// <param name="Subject">From the stored message, for display; null when the message is no longer stored.</param>
public sealed record ActionLogRowDto(
    Guid Id,
    string MessageId,
    string? Subject,
    IReadOnlyList<string> LabelsAdded,
    IReadOnlyList<string> LabelsRemoved,
    IReadOnlyList<string> LabelNamesAdded,
    IReadOnlyList<string> LabelNamesRemoved,
    string? Note,
    Guid? UndoneByBatchId);

/// <param name="Rows">The batch's log in order, at most <see cref="HistoryQuery.MaxRows"/>.</param>
/// <param name="Truncated">The batch has more rows than listed.</param>
/// <param name="CreatedLabels">Labels the batch created (name is the id when Gmail no longer has it); undo keeps them, the user removes them by hand.</param>
public sealed record ActionBatchDetailDto(
    ActionBatchDto Batch, IReadOnlyList<ActionLogRowDto> Rows, bool Truncated, IReadOnlyList<LabelDto> CreatedLabels);
