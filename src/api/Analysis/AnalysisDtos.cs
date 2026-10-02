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
