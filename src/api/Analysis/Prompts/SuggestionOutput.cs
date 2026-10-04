namespace GmailOrganiser.Analysis.Prompts;

/// <summary>
/// One email as the prompt shows it; <see cref="Body"/> is already cleaned and truncated and is never stored.
/// <see cref="Labels"/> are its current user label names, sorted, at most <see cref="MaxLabels"/>.
/// </summary>
public sealed record EmailForPrompt(
    string Id,
    string From,
    string? FromName,
    string? Subject,
    DateTimeOffset Date,
    string? Category,
    bool HasListUnsubscribe,
    bool HasAttachment,
    string Body,
    IReadOnlyList<string> Labels)
{
    public const int MaxLabels = 10;
}

/// <summary>A similar past decision from memory, shown to the model as a hint.</summary>
public sealed record MemoryHint(
    string SenderAddress,
    string? SubjectTemplate,
    string TopicLabel,
    bool NeedsAction,
    bool ToBeDeleted,
    string Outcome,
    double Similarity);

public sealed record PromptInput(
    IReadOnlyList<EmailForPrompt> Emails,
    IReadOnlyList<string> LabelTree,
    IReadOnlyList<MemoryHint> Memory,
    string? AttachmentsSection,
    string ActionLabel,
    string DeleteLabel);

/// <summary>A validated suggestion for one email; <see cref="Confidence"/> is always within [0, 1].</summary>
public sealed record SuggestionOutput(
    string Id,
    string TopicLabel,
    bool IsNewLabel,
    bool NeedsAction,
    bool ToBeDeleted,
    bool UnsubscribeSuggested,
    double Confidence,
    string Reason)
{
    /// <summary>Current labels of the email that <see cref="TopicLabel"/> replaces; empty when they stay.</summary>
    public IReadOnlyList<string> ReplaceLabels { get; init; } = [];
}

/// <summary>A sender-level Gmail filter suggestion; at least one field is set.</summary>
public sealed record FilterCriteriaOutput(string? From, string? ListId, string? SubjectContains);

public sealed record ParsedSuggestions(
    IReadOnlyList<SuggestionOutput> Valid,
    IReadOnlyList<string> Errors,
    FilterCriteriaOutput? Filter);

public sealed record PromptTemplateDto(string Version, string Template);
