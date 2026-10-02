namespace GmailOrganiser.Settings;

public sealed record SettingsDto(
    string OllamaBaseUrl,
    string? ChatModel,
    string? EmbeddingModel,
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
    AttachmentSettings Attachments)
{
    public static SettingsDto From(AppSettings s, GoogleClientCredentials google) => new(
        s.OllamaBaseUrl,
        s.ChatModel,
        s.EmbeddingModel,
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
        s.Attachments);
}

/// <summary>Never carries the secret itself.</summary>
public sealed record GoogleClientDto(string? ClientId, bool SecretSet, bool LockedByEnv);

/// <summary>
/// Partial update: <c>null</c> leaves a value unchanged; an empty model name or a blank prompt template clears it.
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
    UpdateAttachmentSettingsRequest? Attachments = null);

/// <summary>
/// Partial update of <see cref="AttachmentSettings"/>: <c>null</c> leaves a value unchanged, and <see cref="Types"/>
/// changes only the types it lists.
/// </summary>
public sealed record UpdateAttachmentSettingsRequest(
    bool? Enabled = null,
    IReadOnlyList<AttachmentTypeSettingRequest>? Types = null,
    int? MaxBytes = null,
    int? MaxImageBytes = null,
    int? MaxChars = null,
    int? MaxPerMessage = null);

/// <param name="Type">An <see cref="Analysis.Attachments.AttachmentType"/> snake_case name; a string so an unknown name is a field error.</param>
public sealed record AttachmentTypeSettingRequest(string? Type, bool? Enabled);

public sealed record GoogleClientRequest(string? ClientId, string? ClientSecret);
