using GmailOrganiser.Gmail;
using GmailOrganiser.Llm;
using GmailOrganiser.Settings;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Setup;

/// <summary>What the setup wizard and the "setup incomplete" banner need. <see cref="Complete"/> = Gmail connected and a chat model selected.</summary>
public sealed record SetupStatusDto(
    bool GoogleClientConfigured,
    bool GmailConnected,
    bool GmailReauthRequired,
    bool OllamaReachable,
    bool ChatModelSelected,
    bool EmbeddingModelSelected,
    bool WizardSeen,
    bool Complete);

/// <summary>Composes the setup status from the settings, the token store and an Ollama ping; stores nothing.</summary>
public sealed class SetupStatusService(
    ISettingsStore settings,
    ITokenStore tokens,
    IOllamaCatalog ollama,
    GoogleClientService googleClient,
    IOptions<GmailOptions> gmail,
    TimeProvider time,
    ILogger<SetupStatusService> logger)
{
    /// <summary>How long the status waits for Ollama before reporting it unreachable.</summary>
    public static readonly TimeSpan OllamaPingTimeout = TimeSpan.FromSeconds(2);

    public async Task<SetupStatusDto> GetAsync(CancellationToken ct = default)
    {
        var current = await settings.GetAsync(ct);
        var token = await tokens.GetAsync(ct);
        var ollamaReachable = await PingOllamaAsync(ct);

        // With the fake Gmail no Google client is needed to connect, so the wizard step counts as done.
        var client = googleClient.Resolve(current);
        var googleClientConfigured = gmail.Value.UseFake
            || (!string.IsNullOrWhiteSpace(client.ClientId) && !string.IsNullOrWhiteSpace(client.ClientSecret));

        var gmailConnected = token is not null && !token.ReauthRequired;
        var chatModelSelected = !string.IsNullOrWhiteSpace(current.ChatModel);

        return new SetupStatusDto(
            GoogleClientConfigured: googleClientConfigured,
            GmailConnected: gmailConnected,
            GmailReauthRequired: token?.ReauthRequired ?? false,
            OllamaReachable: ollamaReachable,
            ChatModelSelected: chatModelSelected,
            EmbeddingModelSelected: !string.IsNullOrWhiteSpace(current.EmbeddingModel),
            WizardSeen: current.SetupWizardSeen,
            Complete: gmailConnected && chatModelSelected);
    }

    // Any failure (unreachable, not Ollama, invalid saved URL, timeout) is "not reachable"; only the caller's abort propagates.
    private async Task<bool> PingOllamaAsync(CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(OllamaPingTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await ollama.PingAsync(ct: linked.Token);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation("Ollama is not reachable for the setup status ({Error})", ex.GetType().Name);
            return false;
        }
    }
}
