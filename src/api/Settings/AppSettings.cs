using GmailOrganiser.Analysis.Attachments;

namespace GmailOrganiser.Settings;

/// <summary>
/// The user-editable settings document (one <c>jsonb</c> row in <c>settings</c>).
/// Missing or unknown JSON properties fall back to the defaults, so a new setting needs no migration.
/// </summary>
public sealed record AppSettings
{
    public const string FallbackOllamaBaseUrl = "http://host.docker.internal:11434";

    public string OllamaBaseUrl { get; init; } = FallbackOllamaBaseUrl;
    public string? ChatModel { get; init; }
    public string? EmbeddingModel { get; init; }

    /// <summary>The Ollama vision model for <see cref="ImageMode.Vision"/> image reading (#72); <c>null</c> when none is chosen.</summary>
    public string? VisionModel { get; init; }

    /// <summary>
    /// The label an "action" suggestion applies. Resolved by name at apply time: changing it renames nothing in Gmail and
    /// re-labels nothing already stored; the next apply uses (and if needed creates) the new label.
    /// </summary>
    public string ActionLabelName { get; init; } = "Action/ToDo";

    /// <summary>
    /// The label a "delete" suggestion applies and the Clean-up list reads. Resolved by name at apply and query time: changing
    /// it renames nothing in Gmail and re-labels nothing already stored; mail under the old label leaves the Clean-up list.
    /// </summary>
    public string DeleteLabelName { get; init; } = "To-Be-Deleted";

    /// <summary>
    /// Parent label of document-type labels; null = feature off. Renaming touches nothing in Gmail or stored data.
    /// </summary>
    public string? DocumentTypeParent { get; init; }
    public string? GoogleClientId { get; init; }

    /// <summary>Data Protection ciphertext of the Google OAuth client secret; never returned by the API.</summary>
    public string? GoogleClientSecretProtected { get; init; }

    public bool SetupWizardSeen { get; init; }

    /// <summary>Set by the setup status the first time setup is complete; never reset, so the Setup nav stays hidden.</summary>
    public bool SetupCompletedOnce { get; init; }

    /// <summary>Messages per mailbox fetch chunk (one checkpoint each); see <see cref="SettingsValidation"/> for the range.</summary>
    public int FetchChunkSize { get; init; } = DefaultFetchChunkSize;

    public const int DefaultFetchChunkSize = 100;

    // Analysis (epic #22); ranges are in SettingsValidation.
    public int AnalysisDefaultCount { get; init; } = DefaultAnalysisDefaultCount;
    public int AnalysisBodyMaxChars { get; init; } = DefaultAnalysisBodyMaxChars;
    public AnalysisGroupingMode AnalysisGroupingMode { get; init; } = DefaultAnalysisGroupingMode;
    public int AnalysisRepresentativesPerGroup { get; init; } = DefaultAnalysisRepresentativesPerGroup;
    public int AnalysisMinGroupSize { get; init; } = DefaultAnalysisMinGroupSize;

    /// <summary>Subtracted from the representatives' confidence for suggestions copied to the rest of a group.</summary>
    public double AnalysisDerivedConfidencePenalty { get; init; } = DefaultAnalysisDerivedConfidencePenalty;

    /// <summary>Maximum cosine distance between embeddings in one <see cref="AnalysisGroupingMode.Auto"/> cluster.</summary>
    public double AnalysisClusterDistance { get; init; } = DefaultAnalysisClusterDistance;

    /// <summary>
    /// One-off senders per snippet-only prompt (#376); 1 is off. Answers below <see cref="AnalysisPackRetryThreshold"/>
    /// are asked again one by one with the body.
    /// </summary>
    public int AnalysisPackSize { get; init; } = DefaultAnalysisPackSize;
    public double AnalysisPackRetryThreshold { get; init; } = DefaultAnalysisPackRetryThreshold;

    /// <summary>Skip the LLM when memory has <see cref="AnalysisMemoryMinApprovals"/> matching approvals.</summary>
    public bool AnalysisMemoryShortCircuit { get; init; } = true;
    public int AnalysisMemoryMinApprovals { get; init; } = DefaultAnalysisMemoryMinApprovals;
    public double BulkApproveThreshold { get; init; } = DefaultBulkApproveThreshold;

    /// <summary>
    /// Archive an applied needs-action message once its action label is removed in Gmail (off by default). Checked by
    /// the incremental fetch for the messages it re-reads, so turning it on archives nothing retroactively.
    /// </summary>
    public bool AutoArchiveOnActionDone { get; init; }

    /// <summary>
    /// Newly fetched mail of an approved sender policy gets the policy's approved suggestion and is applied without the
    /// LLM (#360). Off: it waits for the policy's next apply.
    /// </summary>
    public bool PolicyAutoApplyFetched { get; init; } = true;

    /// <summary>User override of the analysis prompt; <c>null</c> means the built-in template.</summary>
    public string? AnalysisPromptTemplate { get; init; }

    /// <summary>Attachment types and limits for the analysis (#68); see <see cref="AttachmentSettings"/>.</summary>
    public AttachmentSettings Attachments { get; init; } = new();

    /// <summary>The protection rule toggles (#176); see <see cref="ProtectionSettings"/>.</summary>
    public ProtectionSettings Protection { get; init; } = new();

    /// <summary>The Apps Script auto-archive config (#210); see <see cref="AppsScriptSettings"/>.</summary>
    public AppsScriptSettings AppsScript { get; init; } = new();

    /// <summary>The retention sweep (#368); see <see cref="RetentionSettings"/>.</summary>
    public RetentionSettings Retention { get; init; } = new();

    // Claude review (epic #23); ranges are in SettingsValidation.
    public ClaudeReviewerMode ClaudeReviewerMode { get; init; } = DefaultClaudeReviewerMode;

    /// <summary>Mark suggestions below <see cref="ClaudeSuggestThreshold"/> as worth a Claude review (a UI hint; nothing is sent automatically).</summary>
    public bool ClaudeSuggestLowConfidence { get; init; }
    public double ClaudeSuggestThreshold { get; init; } = DefaultClaudeSuggestThreshold;

    /// <summary>Mark suggestions that propose a new label as worth a Claude review.</summary>
    public bool ClaudeSuggestNewLabels { get; init; }
    public int ClaudeRunTimeoutSeconds { get; init; } = DefaultClaudeRunTimeoutSeconds;
    public int ClaudeMaxItemsPerRun { get; init; } = DefaultClaudeMaxItemsPerRun;
    public int ClaudeMaxTurns { get; init; } = DefaultClaudeMaxTurns;

    /// <summary>Optional <c>--model</c> for the Claude Code CLI; <c>null</c> means the subscription default.</summary>
    public string? ClaudeModel { get; init; }

    /// <summary>A filter review reports a filter with no stored message newer than this many days (#213).</summary>
    public int RulesStaleFilterDays { get; init; } = DefaultRulesStaleFilterDays;

    /// <summary>Sent as Ollama's <c>num_ctx</c> with every analysis call (#353), so a long prompt is not silently truncated.</summary>
    public int LlmNumCtx { get; init; } = DefaultLlmNumCtx;

    /// <summary>
    /// A smaller chat model asked first (#375); <c>null</c> is off, and so is the name of <see cref="ChatModel"/> or a
    /// compare run. Its answer is kept when every email has a valid answer at or above
    /// <see cref="TriageConfidenceThreshold"/>, else <see cref="ChatModel"/> answers the same prompt.
    /// </summary>
    public string? TriageModel { get; init; }
    public double TriageConfidenceThreshold { get; init; } = DefaultTriageConfidenceThreshold;

    /// <summary>The taxonomy proposal (#366) profiles this many canonical senders, most messages first.</summary>
    public int TaxonomyMaxSenders { get; init; } = DefaultTaxonomyMaxSenders;

    /// <summary>The taxonomy proposal keeps at most this many labels.</summary>
    public int TaxonomyMaxLabels { get; init; } = DefaultTaxonomyMaxLabels;

    /// <summary>
    /// The approved label set (#367): the analysis picks topic labels from the label tree only, a new one is a proposal
    /// the person approves one by one (never bulk-approved), and at most <see cref="AnalysisMaxNewLabelsPerRun"/> new
    /// labels per run keep their confidence.
    /// </summary>
    public bool TaxonomyLocked { get; init; }
    public int AnalysisMaxNewLabelsPerRun { get; init; } = DefaultAnalysisMaxNewLabelsPerRun;

    /// <summary>Label names the analysis never uses, as any level of a label path (#367); user data, empty by default.</summary>
    public IReadOnlyList<string> AnalysisBlockedLabels { get; init; } = [];

    /// <summary>Data Protection ciphertext of the <c>/mcp</c> bearer token (#164); never returned by the settings API.</summary>
    public string? McpTokenProtected { get; init; }

    public const int DefaultAnalysisDefaultCount = 20;
    public const int DefaultAnalysisBodyMaxChars = 4000;
    public const AnalysisGroupingMode DefaultAnalysisGroupingMode = AnalysisGroupingMode.Auto;
    public const int DefaultAnalysisRepresentativesPerGroup = 3;
    public const int DefaultAnalysisMinGroupSize = 3;
    public const double DefaultAnalysisDerivedConfidencePenalty = 0.10;
    public const double DefaultAnalysisClusterDistance = 0.15;
    public const int DefaultAnalysisPackSize = 16;
    public const double DefaultAnalysisPackRetryThreshold = 0.60;
    public const int DefaultAnalysisMemoryMinApprovals = 3;
    public const double DefaultBulkApproveThreshold = 0.80;
    public const ClaudeReviewerMode DefaultClaudeReviewerMode = ClaudeReviewerMode.Off;
    public const double DefaultClaudeSuggestThreshold = 0.60;
    public const int DefaultClaudeRunTimeoutSeconds = 600;
    public const int DefaultClaudeMaxItemsPerRun = 10;
    public const int DefaultClaudeMaxTurns = 80;
    public const int DefaultRulesStaleFilterDays = 365;
    public const int DefaultLlmNumCtx = 8192;
    public const double DefaultTriageConfidenceThreshold = 0.70;
    public const int DefaultTaxonomyMaxSenders = 80;
    public const int DefaultTaxonomyMaxLabels = 25;
    public const int DefaultAnalysisMaxNewLabelsPerRun = 3;

    /// <summary>Code defaults overlaid with the <c>.env</c> first-run defaults.</summary>
    public static AppSettings Defaults(SettingsEnvOptions env) => new()
    {
        OllamaBaseUrl = string.IsNullOrWhiteSpace(env.OllamaBaseUrl) ? FallbackOllamaBaseUrl : env.OllamaBaseUrl.Trim(),
    };
}

/// <summary>The single <c>settings</c> row (<see cref="Id"/> is always <see cref="SingletonId"/>).</summary>
public sealed class SettingsRow
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public string Document { get; set; } = "{}";
    public DateTimeOffset? UpdatedAt { get; set; }
}
