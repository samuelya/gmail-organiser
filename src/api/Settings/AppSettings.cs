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
    public string ActionLabelName { get; init; } = "Action/ToDo";
    public string DeleteLabelName { get; init; } = "To-Be-Deleted";
    public string? GoogleClientId { get; init; }

    /// <summary>Data Protection ciphertext of the Google OAuth client secret; never returned by the API.</summary>
    public string? GoogleClientSecretProtected { get; init; }

    public bool SetupWizardSeen { get; init; }

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

    /// <summary>Skip the LLM when memory has <see cref="AnalysisMemoryMinApprovals"/> matching approvals.</summary>
    public bool AnalysisMemoryShortCircuit { get; init; } = true;
    public int AnalysisMemoryMinApprovals { get; init; } = DefaultAnalysisMemoryMinApprovals;
    public double BulkApproveThreshold { get; init; } = DefaultBulkApproveThreshold;

    /// <summary>
    /// Archive an applied needs-action message once its action label is removed in Gmail (off by default). Checked by
    /// the incremental fetch for the messages it re-reads, so turning it on archives nothing retroactively.
    /// </summary>
    public bool AutoArchiveOnActionDone { get; init; }

    /// <summary>User override of the analysis prompt; <c>null</c> means the built-in template.</summary>
    public string? AnalysisPromptTemplate { get; init; }

    /// <summary>Attachment types and limits for the analysis (#68); see <see cref="AttachmentSettings"/>.</summary>
    public AttachmentSettings Attachments { get; init; } = new();

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

    public const int DefaultAnalysisDefaultCount = 20;
    public const int DefaultAnalysisBodyMaxChars = 4000;
    public const AnalysisGroupingMode DefaultAnalysisGroupingMode = AnalysisGroupingMode.Auto;
    public const int DefaultAnalysisRepresentativesPerGroup = 3;
    public const int DefaultAnalysisMinGroupSize = 3;
    public const double DefaultAnalysisDerivedConfidencePenalty = 0.10;
    public const double DefaultAnalysisClusterDistance = 0.15;
    public const int DefaultAnalysisMemoryMinApprovals = 3;
    public const double DefaultBulkApproveThreshold = 0.80;
    public const ClaudeReviewerMode DefaultClaudeReviewerMode = ClaudeReviewerMode.Off;
    public const double DefaultClaudeSuggestThreshold = 0.60;
    public const int DefaultClaudeRunTimeoutSeconds = 600;
    public const int DefaultClaudeMaxItemsPerRun = 10;
    public const int DefaultClaudeMaxTurns = 80;

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
