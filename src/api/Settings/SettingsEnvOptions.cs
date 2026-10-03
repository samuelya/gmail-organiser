using Microsoft.Extensions.Configuration;

namespace GmailOrganiser.Settings;

/// <summary>
/// First-run defaults and locked secrets from the environment (<c>.env</c>), bound from the configuration root.
/// </summary>
public sealed class SettingsEnvOptions
{
    [ConfigurationKeyName("OLLAMA_BASE_URL")]
    public string? OllamaBaseUrl { get; set; }

    [ConfigurationKeyName("GOOGLE_CLIENT_ID")]
    public string? GoogleClientId { get; set; }

    [ConfigurationKeyName("GOOGLE_CLIENT_SECRET")]
    public string? GoogleClientSecret { get; set; }

    /// <summary>A Google client value in <c>.env</c> locks both Google client fields in the UI.</summary>
    public bool GoogleClientLockedByEnv =>
        !string.IsNullOrWhiteSpace(GoogleClientId) || !string.IsNullOrWhiteSpace(GoogleClientSecret);

    /// <summary>
    /// Whether <see cref="ClaudeCodeOAuthTokenKey"/> is non-empty. Set from configuration in
    /// <see cref="SettingsEndpoints.AddSettings"/>; the token itself is never bound, stored, returned or logged.
    /// </summary>
    public bool ClaudeCodeOAuthTokenSet { get; set; }

    public const string ClaudeCodeOAuthTokenKey = "CLAUDE_CODE_OAUTH_TOKEN";
}
