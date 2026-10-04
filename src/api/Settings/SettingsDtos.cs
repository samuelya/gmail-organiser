namespace GmailOrganiser.Settings;

public sealed record SettingsDto(
    string OllamaBaseUrl,
    string? ChatModel,
    string? EmbeddingModel,
    string? VisionModel,
    string ActionLabelName,
    string DeleteLabelName,
    bool SetupWizardSeen,
    int FetchChunkSize,
    GoogleClientDto GoogleClient,
    int AnalysisDefaultCount,
    int AnalysisBodyMaxChars,
    AnalysisGroupingMode AnalysisGroupingMode,
    int AnalysisRepresentativesPerGroup,
    int AnalysisMinGroupSize,
    double AnalysisDerivedConfidencePenalty,
    double AnalysisClusterDistance,
    bool AnalysisMemoryShortCircuit,
    int AnalysisMemoryMinApprovals,
    double BulkApproveThreshold,
    bool AutoArchiveOnActionDone,
    string? AnalysisPromptTemplate,
    AttachmentSettings Attachments,
    ProtectionSettings Protection,
    ClaudeReviewerMode ClaudeReviewerMode,
    bool ClaudeSuggestLowConfidence,
    double ClaudeSuggestThreshold,
    bool ClaudeSuggestNewLabels,
    int ClaudeRunTimeoutSeconds,
    int ClaudeMaxItemsPerRun,
    int ClaudeMaxTurns,
    string? ClaudeModel,
    bool ClaudeTokenSet,
    AppsScriptSettings AppsScript)
{
    /// <param name="claudeTokenSet">Whether <c>CLAUDE_CODE_OAUTH_TOKEN</c> is set; the token itself is never returned.</param>
    public static SettingsDto From(AppSettings s, GoogleClientCredentials google, bool claudeTokenSet) => new(
        s.OllamaBaseUrl,
        s.ChatModel,
        s.EmbeddingModel,
        s.VisionModel,
        s.ActionLabelName,
        s.DeleteLabelName,
        s.SetupWizardSeen,
        s.FetchChunkSize,
        new GoogleClientDto(google.ClientId, google.ClientSecret is not null, google.LockedByEnv),
        s.AnalysisDefaultCount,
        s.AnalysisBodyMaxChars,
        s.AnalysisGroupingMode,
        s.AnalysisRepresentativesPerGroup,
        s.AnalysisMinGroupSize,
        s.AnalysisDerivedConfidencePenalty,
        s.AnalysisClusterDistance,
        s.AnalysisMemoryShortCircuit,
        s.AnalysisMemoryMinApprovals,
        s.BulkApproveThreshold,
        s.AutoArchiveOnActionDone,
        s.AnalysisPromptTemplate,
        s.Attachments,
        s.Protection,
        s.ClaudeReviewerMode,
        s.ClaudeSuggestLowConfidence,
        s.ClaudeSuggestThreshold,
        s.ClaudeSuggestNewLabels,
        s.ClaudeRunTimeoutSeconds,
        s.ClaudeMaxItemsPerRun,
        s.ClaudeMaxTurns,
        s.ClaudeModel,
        claudeTokenSet,
        s.AppsScript);
}

/// <summary>Never carries the secret itself.</summary>
public sealed record GoogleClientDto(string? ClientId, bool SecretSet, bool LockedByEnv);

/// <summary>
/// Partial update: <c>null</c> leaves a value unchanged; an empty model name (including <see cref="ClaudeModel"/>) or a
/// blank prompt template clears it. <see cref="AppsScript"/> replaces the whole saved block.
/// </summary>
public sealed record UpdateSettingsRequest(
    string? OllamaBaseUrl,
    string? ChatModel,
    string? EmbeddingModel,
    bool? SetupWizardSeen,
    int? FetchChunkSize = null,
    int? AnalysisDefaultCount = null,
    int? AnalysisBodyMaxChars = null,
    AnalysisGroupingMode? AnalysisGroupingMode = null,
    int? AnalysisRepresentativesPerGroup = null,
    int? AnalysisMinGroupSize = null,
    double? AnalysisDerivedConfidencePenalty = null,
    double? AnalysisClusterDistance = null,
    bool? AnalysisMemoryShortCircuit = null,
    int? AnalysisMemoryMinApprovals = null,
    double? BulkApproveThreshold = null,
    bool? AutoArchiveOnActionDone = null,
    string? AnalysisPromptTemplate = null,
    UpdateAttachmentSettingsRequest? Attachments = null,
    string? VisionModel = null,
    ClaudeReviewerMode? ClaudeReviewerMode = null,
    bool? ClaudeSuggestLowConfidence = null,
    double? ClaudeSuggestThreshold = null,
    bool? ClaudeSuggestNewLabels = null,
    int? ClaudeRunTimeoutSeconds = null,
    int? ClaudeMaxItemsPerRun = null,
    int? ClaudeMaxTurns = null,
    string? ClaudeModel = null,
    UpdateProtectionSettingsRequest? Protection = null,
    string? ActionLabelName = null,
    string? DeleteLabelName = null,
    AppsScriptSettings? AppsScript = null);

/// <summary>
/// Partial update of <see cref="AttachmentSettings"/>: <c>null</c> leaves a value unchanged, and <see cref="Types"/>
/// changes only the types it lists. <see cref="ImageMode"/> is <c>ocr</c> or <c>vision</c>; a string so another value is a
/// field error.
/// </summary>
public sealed record UpdateAttachmentSettingsRequest(
    bool? Enabled = null,
    IReadOnlyList<AttachmentTypeSettingRequest>? Types = null,
    int? MaxBytes = null,
    int? MaxImageBytes = null,
    int? MaxChars = null,
    int? MaxPerMessage = null,
    string? ImageMode = null);

/// <summary>Partial update of <see cref="ProtectionSettings"/>: <c>null</c> leaves a rule unchanged.</summary>
public sealed record UpdateProtectionSettingsRequest(
    bool? Attachments = null,
    bool? Starred = null,
    bool? Important = null,
    bool? RepliedThreads = null);

/// <param name="Type">An <see cref="Analysis.Attachments.AttachmentType"/> snake_case name; a string so an unknown name is a field error.</param>
public sealed record AttachmentTypeSettingRequest(string? Type, bool? Enabled);

public sealed record GoogleClientRequest(string? ClientId, string? ClientSecret);

/// <summary>Purges local data when <see cref="Confirm"/> is <see cref="ConfirmationWord"/> (trimmed, case-sensitive).</summary>
public sealed record PurgeRequest(string? Confirm)
{
    public const string ConfirmationWord = "purge";
}

/// <param name="Tables">The tables emptied.</param>
public sealed record PurgeResponse(IReadOnlyList<string> Tables, DateTimeOffset PurgedAt);
