namespace GmailOrganiser.Settings;

public sealed record SettingsDto(
    string OllamaBaseUrl,
    string? ChatModel,
    string? EmbeddingModel,
    string ActionLabelName,
    string DeleteLabelName,
    bool SetupWizardSeen,
    GoogleClientDto GoogleClient)
{
    public static SettingsDto From(AppSettings s, GoogleClientCredentials google) => new(
        s.OllamaBaseUrl,
        s.ChatModel,
        s.EmbeddingModel,
        s.ActionLabelName,
        s.DeleteLabelName,
        s.SetupWizardSeen,
        new GoogleClientDto(google.ClientId, google.ClientSecret is not null, google.LockedByEnv));
}

/// <summary>Never carries the secret itself.</summary>
public sealed record GoogleClientDto(string? ClientId, bool SecretSet, bool LockedByEnv);

/// <summary>Partial update: <c>null</c> leaves a value unchanged; an empty model name clears it.</summary>
public sealed record UpdateSettingsRequest(
    string? OllamaBaseUrl,
    string? ChatModel,
    string? EmbeddingModel,
    bool? SetupWizardSeen);

public sealed record GoogleClientRequest(string? ClientId, string? ClientSecret);
