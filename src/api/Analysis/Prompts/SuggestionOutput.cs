namespace GmailOrganiser.Analysis.Prompts;

/// <summary>
/// One email as the prompt shows it; <see cref="Body"/> is already cleaned and truncated and is never stored.
/// <see cref="Labels"/> are its current personal label names, sorted; the prompt shows at most <see cref="MaxLabels"/>.
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
/// <param name="DocumentTypeDecided">
/// The decision was made under the current document-type parent, so a null <paramref name="DocumentTypeLabel"/> means
/// "no type" rather than unknown.
/// </param>
public sealed record MemoryHint(
    string SenderAddress,
    string? SubjectTemplate,
    string TopicLabel,
    bool NeedsAction,
    bool ToBeDeleted,
    string Outcome,
    double Similarity,
    string? DocumentTypeLabel = null,
    bool DocumentTypeDecided = false,
    MailType? MailType = null);

/// <summary>An approved sender policy of an email in the prompt (#360): what the person decided for its sender, list or domain.</summary>
/// <param name="Scope">sender, list or domain.</param>
/// <param name="TopicLabel">Null for a mixed policy, whose rules decide per message.</param>
public sealed record SenderPolicyHint(string Scope, string ScopeKey, bool IsMixed, string? TopicLabel, MailType? MailType, string Action);

/// <param name="DocumentTypeParent">The document-type parent label; null or blank tells the model the feature is off.</param>
/// <param name="Policies">Approved policies of the emails' senders, lists or domains, rendered with the memory.</param>
public sealed record PromptInput(
    IReadOnlyList<EmailForPrompt> Emails,
    IReadOnlyList<string> LabelTree,
    IReadOnlyList<MemoryHint> Memory,
    string? AttachmentsSection,
    string ActionLabel,
    string DeleteLabel,
    string? DocumentTypeParent = null,
    IReadOnlyList<SenderPolicyHint>? Policies = null);

/// <summary>A validated suggestion for one email; <see cref="Confidence"/> is always within [0, 1].</summary>
/// <param name="DocumentTypeLabel">The second label under the document-type parent; null when none or the feature is off.</param>
/// <param name="MailType">What kind of email it is (#365); null when the model gave none or an unknown one.</param>
public sealed record SuggestionOutput(
    string Id,
    string TopicLabel,
    bool IsNewLabel,
    bool NeedsAction,
    bool ToBeDeleted,
    bool UnsubscribeSuggested,
    double Confidence,
    string Reason,
    string? DocumentTypeLabel = null,
    MailType? MailType = null)
{
    /// <summary>Current labels of the email that <see cref="TopicLabel"/> replaces; empty when they stay.</summary>
    public IReadOnlyList<string> ReplaceLabels { get; init; } = [];

    /// <summary>The model's <c>proposedNewLabel</c> when it became <see cref="TopicLabel"/>; null otherwise.</summary>
    public string? ProposedNewLabel { get; init; }
}

/// <summary>A sender-level Gmail filter suggestion; at least one field is set.</summary>
public sealed record FilterCriteriaOutput(string? From, string? ListId, string? SubjectContains);

public sealed record ParsedSuggestions(
    IReadOnlyList<SuggestionOutput> Valid,
    IReadOnlyList<string> Errors,
    FilterCriteriaOutput? Filter)
{
    /// <summary>Optional output the parser ignored without failing the email (ids and field names only).</summary>
    public IReadOnlyList<string> Dropped { get; init; } = [];

    /// <summary>Valid answers whose <c>isNewLabel</c> disagreed with the label tree; the computed value was kept.</summary>
    public int NewLabelDisagreements { get; init; }
}

/// <summary>What the analysis parser checks an answer against besides the JSON (#365).</summary>
/// <param name="LabelTree">The run's label tree: <c>isNewLabel</c> is computed from it, never taken from the model.</param>
/// <param name="ConfiguredLabels">The app's action and delete label names; never a <c>proposedNewLabel</c>.</param>
/// <param name="ReadMailType">False for a custom template that does not ask for a mail type: none is stored.</param>
public sealed record SuggestionParseContext(LabelTreeIndex LabelTree, IReadOnlyCollection<string> ConfiguredLabels, bool ReadMailType = true)
{
    public static readonly SuggestionParseContext Default = new(LabelTreeIndex.Empty, []);
}

public sealed record PromptTemplateDto(string Version, string Template);
