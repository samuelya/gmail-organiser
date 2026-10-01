using System.Security.Cryptography;
using GmailOrganiser.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Gmail;

/// <summary>The stored Google connection. Only the refresh token is kept; access tokens live in memory.</summary>
public sealed record OAuthToken(
    string AccountEmail,
    string RefreshToken,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ConnectedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Google refused the refresh token (<c>invalid_grant</c>: revoked or expired); the user must reconnect.</summary>
    public bool ReauthRequired { get; init; }
}

public interface ITokenStore
{
    /// <summary>The connection, or null when not connected (or the stored token can no longer be decrypted).</summary>
    Task<OAuthToken?> GetAsync(CancellationToken ct = default);

    /// <summary>Stores (or replaces) the connection; the refresh token is encrypted before it is written.</summary>
    Task SaveAsync(string accountEmail, string refreshToken, IReadOnlyList<string> scopes, CancellationToken ct = default);

    /// <summary>
    /// Flags the stored connection as unusable after Google answered <c>invalid_grant</c>; the account email is kept
    /// so the UI can say which account to reconnect. The next <see cref="SaveAsync"/> clears the flag.
    /// </summary>
    Task MarkReauthRequiredAsync(CancellationToken ct = default);

    /// <summary>Forgets the connection. Deleting when not connected is a no-op.</summary>
    Task DeleteAsync(CancellationToken ct = default);
}

/// <summary>Single-account token store backed by the <c>oauth_tokens</c> row, encrypted with Data Protection.</summary>
public sealed class TokenStore(
    AppDbContext db,
    IDataProtectionProvider dataProtection,
    TimeProvider time,
    ILogger<TokenStore> logger) : ITokenStore
{
    public const string ProtectorPurpose = "GmailRefreshToken";

    private readonly IDataProtector protector = dataProtection.CreateProtector(ProtectorPurpose);

    public async Task<OAuthToken?> GetAsync(CancellationToken ct = default)
    {
        var row = await db.OAuthTokens.AsNoTracking().SingleOrDefaultAsync(r => r.Id == OAuthTokenRow.SingletonId, ct);
        if (row is null)
        {
            return null;
        }

        try
        {
            var refreshToken = protector.Unprotect(row.RefreshTokenProtected);
            return new OAuthToken(row.AccountEmail, refreshToken, row.Scopes, row.ConnectedAt, row.UpdatedAt)
            {
                ReauthRequired = row.ReauthRequired,
            };
        }
        catch (CryptographicException)
        {
            // Typically a lost key ring (dp-keys volume). The user reconnects.
            logger.LogWarning("The stored Gmail refresh token cannot be decrypted (Data Protection keys changed?); treating Gmail as not connected");
            return null;
        }
    }

    public async Task SaveAsync(string accountEmail, string refreshToken, IReadOnlyList<string> scopes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        ArgumentNullException.ThrowIfNull(scopes);

        var protectedToken = protector.Protect(refreshToken);
        var scopeArray = scopes.ToArray();
        var now = time.GetUtcNow();

        // One atomic upsert, so a repeated OAuth callback is idempotent. connected_at only moves when the account changes.
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO oauth_tokens (id, account_email, refresh_token_protected, scopes, connected_at, updated_at, reauth_required)
            VALUES ({OAuthTokenRow.SingletonId}, {accountEmail}, {protectedToken}, {scopeArray}, {now}, {now}, FALSE)
            ON CONFLICT (id) DO UPDATE SET
                connected_at = CASE WHEN oauth_tokens.account_email = EXCLUDED.account_email
                                    THEN oauth_tokens.connected_at ELSE EXCLUDED.connected_at END,
                account_email = EXCLUDED.account_email,
                refresh_token_protected = EXCLUDED.refresh_token_protected,
                scopes = EXCLUDED.scopes,
                updated_at = EXCLUDED.updated_at,
                reauth_required = FALSE
            """,
            ct);
    }

    public async Task MarkReauthRequiredAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await db.OAuthTokens
            .Where(r => r.Id == OAuthTokenRow.SingletonId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.ReauthRequired, true).SetProperty(r => r.UpdatedAt, now), ct);
    }

    public async Task DeleteAsync(CancellationToken ct = default) =>
        await db.OAuthTokens.Where(r => r.Id == OAuthTokenRow.SingletonId).ExecuteDeleteAsync(ct);
}

/// <summary>The single <c>oauth_tokens</c> row (<see cref="Id"/> is always <see cref="SingletonId"/>).</summary>
public sealed class OAuthTokenRow
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public string AccountEmail { get; set; } = "";

    /// <summary>Data Protection ciphertext (purpose <see cref="TokenStore.ProtectorPurpose"/>).</summary>
    public string RefreshTokenProtected { get; set; } = "";

    public string[] Scopes { get; set; } = [];
    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>See <see cref="OAuthToken.ReauthRequired"/>.</summary>
    public bool ReauthRequired { get; set; }
}
