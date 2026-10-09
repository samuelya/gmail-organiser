using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Llm;

/// <summary>The start check every chat-model job shares (#488), so a run is never queued only to fail in the background.</summary>
public static class ActiveChat
{
    /// <summary>The settings, once the active provider has a chat model and, for the Claude API, a readable key.</summary>
    /// <exception cref="LlmNotConfiguredException">No chat model is chosen, or no usable Claude API key is set.</exception>
    public static async Task<AppSettings> RequireAsync(ISettingsStore store, ClaudeApiKeyService claudeApiKey, CancellationToken ct)
    {
        var settings = await store.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.ActiveChatModel))
        {
            throw new LlmNotConfiguredException(ModelKinds.Chat);
        }

        if (settings.LlmProvider == LlmProvider.ClaudeApi && string.IsNullOrWhiteSpace(await claudeApiKey.GetAsync(settings, ct)))
        {
            throw new LlmNotConfiguredException(ModelKinds.Chat, LlmClientFactory.NoClaudeApiKeyMessage);
        }

        return settings;
    }
}
