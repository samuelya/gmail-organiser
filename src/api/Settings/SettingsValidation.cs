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

    private static void CheckModelName(Dictionary<string, string[]> errors, string field, string? value)
    {
        if (value is not null && (value.Trim().Length > MaxModelNameLength || value.Any(char.IsControl)))
        {
            errors[field] = [$"Must be at most {MaxModelNameLength} characters, without control characters."];
        }
    }
}
