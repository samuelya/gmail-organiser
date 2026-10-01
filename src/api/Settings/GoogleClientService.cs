using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Settings;

/// <summary>The Google OAuth client. <see cref="ClientSecret"/> is for server use only and is never returned by the API.</summary>
public sealed record GoogleClientCredentials(string? ClientId, string? ClientSecret, bool LockedByEnv);

/// <summary>
/// Resolves and saves the Google OAuth client: <c>.env</c> values lock it; otherwise the saved ID and the
/// Data Protection-encrypted secret are used.
/// </summary>
public sealed class GoogleClientService(
    ISettingsStore store,
    IDataProtectionProvider dataProtection,
    IOptions<SettingsEnvOptions> env,
    ILogger<GoogleClientService> logger)
{
    public const string ProtectorPurpose = "GoogleClientSecret";

    private readonly IDataProtector protector = dataProtection.CreateProtector(ProtectorPurpose);

    public async Task<GoogleClientCredentials> GetAsync(CancellationToken ct = default) =>
        Resolve(await store.GetAsync(ct));

    public GoogleClientCredentials Resolve(AppSettings settings)
    {
        var e = env.Value;
        if (e.GoogleClientLockedByEnv)
        {
            return new(Blank(e.GoogleClientId), Blank(e.GoogleClientSecret), LockedByEnv: true);
        }

        return new(settings.GoogleClientId, Unprotect(settings.GoogleClientSecretProtected), LockedByEnv: false);
    }

    /// <summary>Saves the client ID and the encrypted secret; returns false (nothing saved) when <c>.env</c> locks them.</summary>
    public async Task<bool> TrySetAsync(string clientId, string clientSecret, CancellationToken ct = default)
    {
        if (env.Value.GoogleClientLockedByEnv)
        {
            return false;
        }

        var protectedSecret = protector.Protect(clientSecret);
        await store.UpdateAsync(s => s with { GoogleClientId = clientId, GoogleClientSecretProtected = protectedSecret }, ct);
        return true;
    }

    private string? Unprotect(string? ciphertext)
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
            // Typically a lost key ring (dp-keys volume). The user re-enters the secret.
            logger.LogWarning("The saved Google client secret cannot be decrypted (Data Protection keys changed?); treating it as not set");
            return null;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
