namespace GmailOrganiser.Analysis;

/// <summary><c>scope</c> is <c>inbox | all | sender | messages</c>; <c>count</c> defaults to the AnalysisDefaultCount setting.</summary>
public sealed record AnalysisPreviewRequest(string? Scope, string? SenderAddress, int? Count, string[]? MessageIds);

/// <summary>What a run with these settings would cost, without any model call.</summary>
public sealed record GroupingPreviewDto(
    int Messages,
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
