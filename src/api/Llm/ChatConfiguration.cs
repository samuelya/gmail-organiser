using GmailOrganiser.Settings;

namespace GmailOrganiser.Llm;

/// <summary>
/// One settings read checked by <see cref="ILlmClientFactory.EnsureChatConfiguredAsync"/>: a caller names the run's model,
/// picks its limits and builds its client from the same snapshot (#488).
/// </summary>
/// <param name="Model">The active provider's chat model (<see cref="AppSettings.ActiveChatModel"/>).</param>
public sealed record ChatConfiguration(AppSettings Settings, string Model)
{
    /// <summary>The decrypted Claude API key for that provider; internal, so it never appears in <see cref="ToString"/>.</summary>
    internal string? ClaudeApiKey { get; init; }

    /// <summary>True when the chat calls go to Ollama (its <c>num_ctx</c> limit and error wording apply).</summary>
    public bool OnOllama => Settings.LlmProvider == LlmProvider.Ollama;
}
