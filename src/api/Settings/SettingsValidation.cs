using System.Globalization;
using GmailOrganiser.Analysis.Prompts;

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

    public static Dictionary<string, string[]> Validate(UpdateSettingsRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.OllamaBaseUrl is { } url && !IsHttpUrl(url))
        {
            errors["ollamaBaseUrl"] = ["Must be an absolute http or https URL."];
        }

        CheckModelName(errors, "chatModel", request.ChatModel);
        CheckModelName(errors, "embeddingModel", request.EmbeddingModel);
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
