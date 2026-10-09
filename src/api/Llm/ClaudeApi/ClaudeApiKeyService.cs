using System.Security.Cryptography;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.DataProtection;

namespace GmailOrganiser.Llm.ClaudeApi;

/// <summary>
/// The Claude API key, Data Protection-encrypted in the settings document. <see cref="GetAsync"/> is for server use only;
/// the API returns at most <see cref="Hint"/>.
/// </summary>
public sealed class ClaudeApiKeyService(
    ISettingsStore store,
    IDataProtectionProvider dataProtection,
    ILogger<ClaudeApiKeyService> logger)
{
    public const string ProtectorPurpose = "ClaudeApiKey";
    private const int HintLength = 4;

    private readonly IDataProtector protector = dataProtection.CreateProtector(ProtectorPurpose);

    /// <summary>The decrypted key, or <c>null</c> when none is saved or it no longer decrypts.</summary>
    public async Task<string?> GetAsync(CancellationToken ct = default) => await GetAsync(await store.GetAsync(ct), ct);

    /// <summary>The decrypted key from already-loaded settings, or <c>null</c> when none is saved or it no longer decrypts.</summary>
    public async Task<string?> GetAsync(AppSettings settings, CancellationToken ct = default) =>
        await UnprotectAsync(settings.ClaudeApiKeyProtected, ct);

    /// <summary>Saves a validated, trimmed key.</summary>
    public async Task SetAsync(string apiKey, CancellationToken ct = default)
    {
        var ciphertext = protector.Protect(apiKey);
        await store.UpdateAsync(s => s with { ClaudeApiKeyProtected = ciphertext }, ct);
        logger.LogInformation("Claude API key saved");
    }

    /// <summary>Removes the saved key; does nothing when none is saved.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        // The read skips the locked write when there is nothing to clear; the locked check covers a concurrent clear.
        if ((await store.GetAsync(ct)).ClaudeApiKeyProtected is not null && await ClearIfAsync(saved => saved is not null, ct))
        {
            logger.LogInformation("Claude API key cleared");
        }
    }

    /// <summary><c>…</c> and the key's last four characters, or <c>null</c> when no usable key is saved.</summary>
    public async Task<string?> HintAsync(AppSettings settings, CancellationToken ct = default) =>
        await UnprotectAsync(settings.ClaudeApiKeyProtected, ct) is { Length: >= HintLength } key ? "…" + key[^HintLength..] : null;

    private async Task<string?> UnprotectAsync(string? ciphertext, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(ciphertext);
        }
        catch (CryptographicException)
        {
            // Typically a lost key ring (dp-keys volume): the stale ciphertext is dropped so the key reads as not set
            // and the warning is logged once, by whichever request removed it. The user re-enters the key.
            if (await ClearIfAsync(saved => saved == ciphertext, ct))
            {
                logger.LogWarning("The saved Claude API key cannot be decrypted (Data Protection keys changed?); removed it, re-enter the key");
            }

            return null;
        }
    }

    /// <summary>Clears the saved ciphertext under the settings row lock when <paramref name="when"/> holds; returns whether it did.</summary>
    private async Task<bool> ClearIfAsync(Func<string?, bool> when, CancellationToken ct)
    {
        var cleared = false;
        await store.UpdateAsync(s =>
        {
            cleared = when(s.ClaudeApiKeyProtected);
            return cleared ? s with { ClaudeApiKeyProtected = null } : s;
        }, ct);
        return cleared;
    }
}
