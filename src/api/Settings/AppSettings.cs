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
    public string ActionLabelName { get; init; } = "Action/ToDo";
    public string DeleteLabelName { get; init; } = "To-Be-Deleted";
    public string? GoogleClientId { get; init; }

    /// <summary>Data Protection ciphertext of the Google OAuth client secret; never returned by the API.</summary>
    public string? GoogleClientSecretProtected { get; init; }

    public bool SetupWizardSeen { get; init; }

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
