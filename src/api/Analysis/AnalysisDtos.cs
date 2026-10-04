namespace GmailOrganiser.Analysis;

/// <summary><c>scope</c> is <c>inbox | all | sender | messages</c>; <c>count</c> defaults to the AnalysisDefaultCount setting.</summary>
public sealed record AnalysisPreviewRequest(string? Scope, string? SenderAddress, int? Count, string[]? MessageIds);

/// <summary>
/// What a run with these settings would cost, without any model call. <c>skipped</c>: ids of a messages scope that
/// are approved, applied, deleted or unknown and so are not analysed (0 for the other scopes).
/// </summary>
public sealed record GroupingPreviewDto(
    int Messages,
    int Skipped,
    int Groups,
    int EstimatedLlmCalls,
    int EstimatedDerived,
    int EstimatedFromMemory,
    bool EmbeddingsAvailable,
    IReadOnlyList<GroupPreviewDto> LargestGroups);

public sealed record GroupPreviewDto(string Key, string SenderAddress, string Display, int Size, int Representatives);

/// <summary>
/// <c>scope</c> as for the preview; <c>count</c> 1–1000 (ignored for the messages scope, which covers its ids);
/// <c>groupingMode</c> (<c>off | sender_subject | auto</c>) overrides the setting for this run.
/// </summary>
public sealed record StartAnalysisRunRequest(
    string? Scope, string? SenderAddress, string[]? MessageIds, int? Count, string? GroupingMode);

/// <summary>One of <c>messageIds</c> (1–500) or <c>senderAddress</c>.</summary>
public sealed record ReanalyseRequest(string[]? MessageIds, string? SenderAddress);

public sealed record ReanalyseResponse(int Reset);

/// <summary>
/// Exactly one of <c>suggestionIds</c> (suggestions of any status) or <c>runId</c> (the suggestions an earlier run wrote,
/// or those a compare run wrote alternatives for); at most the analysis max count messages.
/// </summary>
public sealed record CompareRunRequest(Guid[]? SuggestionIds, Guid? RunId);

/// <summary>
/// One run. <c>kind</c> is <c>analyse</c> or <c>compare</c> (results stored as alternatives, #248). <c>status</c> stays <c>running</c> while its job is paused (the run has no paused status; the job shows
/// it). <c>skippedMessages</c>: ids of a messages scope not analysed because they are approved, applied, deleted or
/// unknown, plus candidates that stopped qualifying before their group ran. <c>savedPercent</c> is
/// <c>1 − llmCalls / max(1, messagesCovered)</c>; retries count as calls, so it can go negative.
/// </summary>
public sealed record AnalysisRunDto(
    Guid Id,
    Guid? JobId,
    string Kind,
    string Scope,
    string? SenderAddress,
    int RequestedCount,
    string GroupingMode,
    string Status,
    int MessagesCovered,
    int MessagesLlm,
    int MessagesDerived,
    int MessagesFromMemory,
    int LlmCalls,
    int Groups,
    int MixedGroups,
    int FailedMessages,
    int SkippedMessages,
    int AttachmentsConverted,
    int AttachmentsSkipped,
    string? Model,
    string? PromptVersion,
    string? Error,
    double SavedPercent,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// Message counts by analysis status, applied action/delete counts, the cumulative LLM savings and the not-analysed
/// messages the labelled scope still covers.
/// </summary>
/// <param name="Alternatives">Suggestions with a compare-run alternative waiting to be accepted or discarded (#249).</param>
public sealed record AnalysisSummaryDto(
    int NotAnalysed,
    int Analysed,
    int Approved,
    int Rejected,
    int Applied,
    int ActionCount,
    int ToBeDeletedCount,
    long TotalLlmCalls,
    long TotalMessagesCovered,
    double SavedPercent,
    int LabelledNotAnalysed,
    int Alternatives);
