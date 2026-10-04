using System.Globalization;
using System.Text.Json;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Gmail;
using GmailOrganiser.Review;

namespace GmailOrganiser.Settings;

/// <summary>Input checks for the settings endpoints; each returns field errors for a validation ProblemDetails.</summary>
public static class SettingsValidation
{
    public const int MaxModelNameLength = 200;
    public const int MaxUrlLength = 2048;
    public const int MaxClientIdLength = 256;
    public const int MaxClientSecretLength = 512;
    public const string GoogleClientIdSuffix = ".apps.googleusercontent.com";
    public const int MinFetchChunkSize = 10;
    public const int MaxFetchChunkSize = 5000;
    public const int MinAnalysisDefaultCount = 1;
    public const int MaxAnalysisDefaultCount = 1000;
    public const int MinAnalysisBodyMaxChars = 500;
    public const int MaxAnalysisBodyMaxChars = 50_000;
    // Two: a group is only derived when at least two representatives agree.
    public const int MinAnalysisRepresentativesPerGroup = 2;
    public const int MaxAnalysisRepresentativesPerGroup = 10;
    public const int MinAnalysisMinGroupSize = 2;
    public const int MaxAnalysisMinGroupSize = 50;
    public const double MinAnalysisDerivedConfidencePenalty = 0;
    public const double MaxAnalysisDerivedConfidencePenalty = 0.5;
    public const double MinAnalysisClusterDistance = 0.02;
    public const double MaxAnalysisClusterDistance = 0.6;
    public const int MinAnalysisMemoryMinApprovals = 1;
    public const int MaxAnalysisMemoryMinApprovals = 20;
    public const double MinBulkApproveThreshold = 0.5;
    public const double MaxBulkApproveThreshold = 1.0;
    public const int MaxAnalysisPromptTemplateLength = 20_000;
    public const int MinAttachmentMaxBytes = 64 * 1024;
    public const int MaxAttachmentMaxBytes = 25 * 1024 * 1024;
    public const int MinAttachmentMaxChars = 500;
    public const int MaxAttachmentMaxChars = 50_000;
    public const int MinAttachmentMaxPerMessage = 1;
    public const int MaxAttachmentMaxPerMessage = 20;
    public const double MinClaudeSuggestThreshold = 0.30;
    public const double MaxClaudeSuggestThreshold = 0.95;
    public const int MinClaudeRunTimeoutSeconds = 60;
    public const int MaxClaudeRunTimeoutSeconds = 3600;
    public const int MinClaudeMaxItemsPerRun = 1;
    public const int MaxClaudeMaxItemsPerRun = 50;
    public const int MinClaudeMaxTurns = 10;
    public const int MaxClaudeMaxTurns = 300;
    public const int MaxLabelNameLength = GmailLimits.LabelNameMaxLength;
    public const string LabelNamesClashField = "deleteLabelName";
    public const string LabelNamesClashMessage = "Must differ from the action label.";

    /// <summary>
    /// Checks <paramref name="request"/>; the label names must differ from each other, so a name sent alone is compared with the
    /// other name in <paramref name="current"/> (the defaults when omitted).
    /// </summary>
    public static Dictionary<string, string[]> Validate(UpdateSettingsRequest request, AppSettings? current = null)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.OllamaBaseUrl is { } url && !IsHttpUrl(url))
        {
            errors["ollamaBaseUrl"] = ["Must be an absolute http or https URL."];
        }

        CheckModelName(errors, "chatModel", request.ChatModel);
        CheckModelName(errors, "embeddingModel", request.EmbeddingModel);
        CheckModelName(errors, "visionModel", request.VisionModel);
        if (request.FetchChunkSize is < MinFetchChunkSize or > MaxFetchChunkSize)
        {
            errors["fetchChunkSize"] = [$"Must be between {MinFetchChunkSize} and {MaxFetchChunkSize}."];
        }

        CheckRange(errors, "analysisDefaultCount", request.AnalysisDefaultCount, MinAnalysisDefaultCount, MaxAnalysisDefaultCount);
        CheckRange(errors, "analysisBodyMaxChars", request.AnalysisBodyMaxChars, MinAnalysisBodyMaxChars, MaxAnalysisBodyMaxChars);
        CheckRange(errors, "analysisRepresentativesPerGroup", request.AnalysisRepresentativesPerGroup,
            MinAnalysisRepresentativesPerGroup, MaxAnalysisRepresentativesPerGroup);
        CheckRange(errors, "analysisMinGroupSize", request.AnalysisMinGroupSize, MinAnalysisMinGroupSize, MaxAnalysisMinGroupSize);
        CheckRange(errors, "analysisDerivedConfidencePenalty", request.AnalysisDerivedConfidencePenalty,
            MinAnalysisDerivedConfidencePenalty, MaxAnalysisDerivedConfidencePenalty);
        CheckRange(errors, "analysisClusterDistance", request.AnalysisClusterDistance, MinAnalysisClusterDistance, MaxAnalysisClusterDistance);
        CheckRange(errors, "analysisMemoryMinApprovals", request.AnalysisMemoryMinApprovals,
            MinAnalysisMemoryMinApprovals, MaxAnalysisMemoryMinApprovals);
        CheckRange(errors, "bulkApproveThreshold", request.BulkApproveThreshold, MinBulkApproveThreshold, MaxBulkApproveThreshold);
        if (request.AnalysisGroupingMode is { } mode && !Enum.IsDefined(mode))
        {
            errors["analysisGroupingMode"] = ["Must be off, sender_subject or auto."];
        }

        CheckModelName(errors, "claudeModel", request.ClaudeModel);
        CheckRange(errors, "claudeSuggestThreshold", request.ClaudeSuggestThreshold, MinClaudeSuggestThreshold, MaxClaudeSuggestThreshold);
        CheckRange(errors, "claudeRunTimeoutSeconds", request.ClaudeRunTimeoutSeconds, MinClaudeRunTimeoutSeconds, MaxClaudeRunTimeoutSeconds);
        CheckRange(errors, "claudeMaxItemsPerRun", request.ClaudeMaxItemsPerRun, MinClaudeMaxItemsPerRun, MaxClaudeMaxItemsPerRun);
        CheckRange(errors, "claudeMaxTurns", request.ClaudeMaxTurns, MinClaudeMaxTurns, MaxClaudeMaxTurns);
        if (request.ClaudeReviewerMode is { } reviewer && !Enum.IsDefined(reviewer))
        {
            errors["claudeReviewerMode"] = ["Must be off, headless_claude_code or claude_desktop."];
        }

        if (request.AnalysisPromptTemplate is { } template
            && (template.Trim().Length > MaxAnalysisPromptTemplateLength || template.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t')))
        {
            errors["analysisPromptTemplate"] =
                [$"Must be at most {MaxAnalysisPromptTemplateLength} characters, without control characters other than newline and tab."];
        }
        else if (request.AnalysisPromptTemplate is { } custom && NormalisePromptTemplate(custom) is not null
                 && !custom.Contains(PromptTemplate.EmailsPlaceholder, StringComparison.Ordinal))
        {
            errors["analysisPromptTemplate"] = [$"Must contain the {PromptTemplate.EmailsPlaceholder} placeholder."];
        }

        if (request.Attachments is { } attachments)
        {
            ValidateAttachments(errors, attachments);
        }

        ValidateLabelNames(errors, request, current ?? new AppSettings());
        return errors;
    }

    public static Dictionary<string, string[]> Validate(GoogleClientRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var id = request.ClientId?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > MaxClientIdLength || id.Length <= GoogleClientIdSuffix.Length
            || !id.EndsWith(GoogleClientIdSuffix, StringComparison.Ordinal) || id.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            errors["clientId"] = [$"Must be a Google OAuth client ID ending with '{GoogleClientIdSuffix}'."];
        }

        var secret = request.ClientSecret?.Trim();
        if (string.IsNullOrEmpty(secret) || secret.Length > MaxClientSecretLength || secret.Any(char.IsControl))
        {
            errors["clientSecret"] = [$"Must be non-empty, at most {MaxClientSecretLength} characters, without control characters."];
        }

        return errors;
    }

    public static bool IsHttpUrl(string value) =>
        value.Length <= MaxUrlLength
        && !value.Any(char.IsControl)
        && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

    /// <summary>Trims a model name; an empty name clears the setting.</summary>
    public static string? NormaliseModelName(string value) => value.Trim() is { Length: > 0 } name ? name : null;

    /// <summary>Trims a prompt template; a blank template clears the override so the built-in one applies.</summary>
    public static string? NormalisePromptTemplate(string value) => value.Trim() is { Length: > 0 } template ? template : null;

    private static void ValidateAttachments(Dictionary<string, string[]> errors, UpdateAttachmentSettingsRequest request)
    {
        CheckRange(errors, "attachments.maxBytes", request.MaxBytes, MinAttachmentMaxBytes, MaxAttachmentMaxBytes);
        CheckRange(errors, "attachments.maxImageBytes", request.MaxImageBytes, MinAttachmentMaxBytes, MaxAttachmentMaxBytes);
        CheckRange(errors, "attachments.maxChars", request.MaxChars, MinAttachmentMaxChars, MaxAttachmentMaxChars);
        CheckRange(errors, "attachments.maxPerMessage", request.MaxPerMessage, MinAttachmentMaxPerMessage, MaxAttachmentMaxPerMessage);
        if (request.ImageMode is not null && !ImageModeJsonConverter.TryParse(request.ImageMode, out _))
        {
            errors["attachments.imageMode"] = ["Must be ocr or vision."];
        }

        var seen = new HashSet<AttachmentType>();
        for (var i = 0; i < (request.Types?.Count ?? 0); i++)
        {
            var entry = request.Types![i];
            var field = $"attachments.types[{i}]";
            if (entry is null || !AttachmentTypeJsonConverter.TryParse(entry.Type, out var type))
            {
                var names = string.Join(", ", Enum.GetValues<AttachmentType>().Select(t => JsonNamingPolicy.SnakeCaseLower.ConvertName(t.ToString())));
                errors[$"{field}.type"] = [$"Unknown attachment type; must be one of {names}."];
            }
            else if (!seen.Add(type.Value))
            {
                errors[$"{field}.type"] = ["Each attachment type may be listed once."];
            }
            else if (entry.Enabled is null)
            {
                errors[$"{field}.enabled"] = ["Required."];
            }
            else if (type == AttachmentType.Archive && entry.Enabled.Value)
            {
                errors[$"{field}.enabled"] = ["Archive attachments (zip, 7z, rar, tar, gz) are never read and cannot be enabled."];
            }
        }
    }

    private static void ValidateLabelNames(Dictionary<string, string[]> errors, UpdateSettingsRequest request, AppSettings current)
    {
        var actionOk = CheckLabelName(errors, "actionLabelName", request.ActionLabelName);
        var deleteOk = CheckLabelName(errors, "deleteLabelName", request.DeleteLabelName);
        if ((request.ActionLabelName is not null || request.DeleteLabelName is not null) && actionOk && deleteOk
            && LabelNamesClash(request.ActionLabelName?.Trim() ?? current.ActionLabelName, request.DeleteLabelName?.Trim() ?? current.DeleteLabelName))
        {
            errors[LabelNamesClashField] = [LabelNamesClashMessage];
        }
    }

    /// <summary>Gmail label names are case-insensitive, so the action and delete labels must differ ignoring case.</summary>
    public static bool LabelNamesClash(string actionLabelName, string deleteLabelName) =>
        string.Equals(actionLabelName, deleteLabelName, StringComparison.OrdinalIgnoreCase);

    // Trimmed, non-empty, a valid user label path with no Gmail system label name at any level, at most 225 characters.
    private static bool CheckLabelName(Dictionary<string, string[]> errors, string field, string? value)
    {
        if (value is null)
        {
            return true;
        }

        var name = value.Trim();
        string? error = name.Length == 0 ? "Required."
            : name.Length > MaxLabelNameLength ? $"Must be at most {MaxLabelNameLength} characters."
            : LabelPath.IsReserved(name) ? "Must not be a Gmail system label."
            : !LabelResolver.IsValid(name) ? "Must be a valid Gmail label: up to five '/'-separated parts of at most 100 characters, none blank or a system label."
            : null;
        if (error is not null)
        {
            errors[field] = [error];
        }

        return error is null;
    }

    private static void CheckRange(Dictionary<string, string[]> errors, string field, int? value, int min, int max)
    {
        if (value is { } v && (v < min || v > max))
        {
            errors[field] = [$"Must be between {min} and {max}."];
        }
    }

    private static void CheckRange(Dictionary<string, string[]> errors, string field, double? value, double min, double max)
    {
        if (value is { } v && (!double.IsFinite(v) || v < min || v > max))
        {
            errors[field] = [string.Create(CultureInfo.InvariantCulture, $"Must be a number between {min} and {max}.")];
        }
    }

    private static void CheckModelName(Dictionary<string, string[]> errors, string field, string? value)
    {
        if (value is not null && (value.Trim().Length > MaxModelNameLength || value.Any(char.IsControl)))
        {
            errors[field] = [$"Must be at most {MaxModelNameLength} characters, without control characters."];
        }
    }
}
