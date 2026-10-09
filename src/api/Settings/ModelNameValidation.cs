namespace GmailOrganiser.Settings;

/// <summary>The shared check and normalisation for every model-name setting (Ollama, Claude Code, Claude API).</summary>
public static class ModelNameValidation
{
    public static void Check(Dictionary<string, string[]> errors, string field, string? value)
    {
        if (value is not null && (value.Trim().Length > SettingsValidation.MaxModelNameLength || value.Any(char.IsControl)))
        {
            errors[field] = [$"Must be at most {SettingsValidation.MaxModelNameLength} characters, without control characters."];
        }
    }

    /// <summary>Trims a model name; an empty name clears the setting.</summary>
    public static string? Normalise(string value) => value.Trim() is { Length: > 0 } name ? name : null;
}
