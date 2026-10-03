using System.Security.Cryptography;
using System.Text;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace GmailOrganiser.Mcp;

/// <summary>
/// The locally generated bearer token for <c>/mcp</c>: 32 random bytes (base64url), stored Data-Protected in
/// <see cref="AppSettings.McpTokenProtected"/>, created on first use and replaced by <see cref="RotateAsync"/>.
/// </summary>
public sealed class McpTokenService(ISettingsStore store, IDataProtectionProvider dataProtection, ILogger<McpTokenService> logger)
{
    public const string ProtectorPurpose = "McpToken";
    public const int TokenBytes = 32;

    private readonly IDataProtector protector = dataProtection.CreateProtector(ProtectorPurpose);

    /// <summary>The current token; null when none was generated yet or it can no longer be decrypted.</summary>
    public async Task<string?> GetAsync(CancellationToken ct = default) =>
        Unprotect((await store.GetAsync(ct)).McpTokenProtected);

    /// <summary>The current token, generating one when there is none (or the saved one cannot be decrypted).</summary>
    public async Task<string> GetOrCreateAsync(CancellationToken ct = default)
    {
        if (await GetAsync(ct) is { } token)
        {
            return token;
        }

        // UpdateAsync runs the change under the settings row lock, so concurrent first uses agree on one token.
        string? created = null;
        var saved = await store.UpdateAsync(s =>
        {
            if (Unprotect(s.McpTokenProtected) is { } existing)
            {
                created = existing;
                return s;
            }

            created = NewToken();
            return s with { McpTokenProtected = protector.Protect(created) };
        }, ct);
        return created ?? Unprotect(saved.McpTokenProtected)!;
    }

    /// <summary>Replaces the token; clients configured with the old one get 401 from then on.</summary>
    public async Task<string> RotateAsync(CancellationToken ct = default)
    {
        var token = NewToken();
        await store.UpdateAsync(s => s with { McpTokenProtected = protector.Protect(token) }, ct);
        return token;
    }

    /// <summary>Whether <paramref name="presented"/> equals the current token, compared in constant time.</summary>
    public async Task<bool> IsValidAsync(string? presented, CancellationToken ct = default) =>
        !string.IsNullOrEmpty(presented)
        && await GetAsync(ct) is { } token
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(token));

    private static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));

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
            // Typically a lost key ring (dp-keys volume). A new token is generated; the user re-copies the snippet.
            logger.LogWarning("The saved MCP token cannot be decrypted (Data Protection keys changed?); treating it as not set");
            return null;
        }
    }
}
