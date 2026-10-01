using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace GmailOrganiser.Gmail.Auth;

/// <summary>Builds Google's consent URL (authorization code flow with PKCE S256) and the random values it needs.</summary>
public static class GoogleAuthorizationRequest
{
    public const string CallbackPath = "/api/auth/google/callback";

    /// <summary>A URL-safe random value with 256 bits of entropy (used for <c>state</c> and the PKCE verifier).</summary>
    public static string NewRandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>PKCE S256: <c>BASE64URL(SHA256(ASCII(verifier)))</c> (RFC 7636 §4.2).</summary>
    public static string ChallengeFor(string codeVerifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    public static string RedirectUri(string appBaseUrl) => appBaseUrl.TrimEnd('/') + CallbackPath;

    public static string BuildUrl(string authorizationEndpoint, string clientId, string redirectUri, string state, string codeVerifier) =>
        QueryHelpers.AddQueryString(authorizationEndpoint, new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', GmailScopes.All),
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "true",
            ["state"] = state,
            ["code_challenge"] = ChallengeFor(codeVerifier),
            ["code_challenge_method"] = "S256",
        });

    private static string Base64Url(byte[] bytes) => WebEncoders.Base64UrlEncode(bytes);
}
