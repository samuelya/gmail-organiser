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
/// One run. <c>status</c> stays <c>running</c> while its job is paused (the run has no paused status; the job shows
/// it). <c>skippedMessages</c>: ids of a messages scope not analysed because they are approved, applied, deleted or
/// unknown, plus candidates that stopped qualifying before their group ran. <c>savedPercent</c> is
/// <c>1 − llmCalls / max(1, messagesCovered)</c>; retries count as calls, so it can go negative.
/// </summary>
public sealed record AnalysisRunDto(
    Guid Id,
    Guid? JobId,
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
    string? Model,
    string? PromptVersion,
    string? Error,
    double SavedPercent,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>Message counts by analysis status, applied action/delete counts and the cumulative LLM savings.</summary>
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
    double SavedPercent);
