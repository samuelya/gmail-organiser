namespace GmailOrganiser.Analysis;

/// <summary>
/// <c>scope</c> is <c>inbox | all | sender | messages | labelled | top_senders</c>; <c>count</c> defaults to the
/// AnalysisDefaultCount setting (senders, at most 100, for <c>top_senders</c>, whose optional <c>senderAddress</c>
/// targets one sender).
/// </summary>
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
    IReadOnlyList<GroupPreviewDto> LargestGroups,
    IReadOnlyList<PolicyCandidateDto>? Senders = null);

/// <summary>A sender a <c>top_senders</c> run would propose a policy for; <c>scope</c> is <c>sender</c> or <c>list</c>.</summary>
public sealed record PolicyCandidateDto(string Scope, string ScopeKey, string? DisplayName, int Count);

public sealed record GroupPreviewDto(string Key, string SenderAddress, string Display, int Size, int Representatives);

/// <summary>
/// <c>scope</c> as for the preview; <c>count</c> 1–1000 (ignored for the messages scope, which covers its ids; senders,
/// 1–100, for <c>top_senders</c>, where <c>senderAddress</c>, raw or canonical, optionally targets one sender);
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
/// <c>1 − llmCalls / max(1, messagesCovered)</c>; retries count as calls, so it can go negative. Tokens and
/// <c>llmMilliseconds</c> (<c>llmSeconds</c>) are summed over the model calls; <c>nearContextLimit</c> counts calls whose
/// prompt filled at least 90 % of <c>num_ctx</c> (#353). <c>isStalled</c>: queued or running, but no queued, running or
/// paused job is behind it (start-up recovery marks such runs failed; <c>POST /runs/{id}/resume</c> continues them, #378).
/// A <c>top_senders</c> run counts one group and one call per sender, <c>policiesProposed</c> the policies it stored,
/// <c>messagesCovered</c> their senders' messages and <c>failedMessages</c> the senders whose output was invalid (#357).
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
    DateTimeOffset? FinishedAt,
    long PromptTokens,
    long CompletionTokens,
    long LlmMilliseconds,
    double LlmSeconds,
    int NearContextLimit,
    int TriageCalls,
    int EscalatedCalls,
    bool IsStalled,
    int PoliciesProposed);

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
