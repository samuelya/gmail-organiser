namespace GmailOrganiser.Gmail.Auth;

/// <summary>The Google OAuth calls the connect flow makes; a test seam over the token, revoke and profile endpoints.</summary>
public interface IGoogleOAuthClient
{
    /// <summary>Exchanges an authorization code (with its PKCE verifier) for tokens.</summary>
    /// <exception cref="GoogleOAuthException">Google refused the exchange or answered with an unusable response.</exception>
    Task<GoogleTokenResult> ExchangeCodeAsync(GoogleCodeExchange exchange, CancellationToken ct);

    /// <summary>The Gmail address of the account that granted <paramref name="accessToken"/> (Gmail <c>users.getProfile</c>).</summary>
    Task<string> GetAccountEmailAsync(string accessToken, CancellationToken ct);

    /// <summary>Revokes a refresh token (and with it the whole grant) at Google.</summary>
    Task RevokeAsync(string refreshToken, CancellationToken ct);
}

public sealed record GoogleCodeExchange(string Code, string CodeVerifier, string RedirectUri, string ClientId, string ClientSecret);

/// <param name="RefreshToken">Null when Google did not issue one (it should with <c>prompt=consent</c>).</param>
/// <param name="Scopes">The scopes actually granted.</param>
public sealed record GoogleTokenResult(string AccessToken, string? RefreshToken, IReadOnlyList<string> Scopes);

/// <summary>A Google OAuth call failed. <see cref="Error"/> is Google's error code (never a token), when there is one.</summary>
public sealed class GoogleOAuthException(string message, string? error = null, Exception? inner = null) : Exception(message, inner)
{
    public string? Error { get; } = error;
}
