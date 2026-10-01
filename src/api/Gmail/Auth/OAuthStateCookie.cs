using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Gmail.Auth;

/// <summary>The pending connect flow: the <c>state</c> sent to Google, the PKCE verifier and when the flow expires.</summary>
public sealed record OAuthPendingFlow(string State, string CodeVerifier, DateTimeOffset ExpiresAt);

/// <summary>
/// Keeps the pending flow in one short-lived, HttpOnly, SameSite=Lax cookie encrypted with Data Protection.
/// Lax is enough: Google's return to the callback is a top-level GET navigation.
/// </summary>
public sealed class OAuthStateCookie(IDataProtectionProvider dataProtection, TimeProvider time, IOptions<GoogleOAuthOptions> options)
{
    public const string CookieName = "gmo_google_oauth";
    public const string ProtectorPurpose = "GoogleOAuthState";
    private const string CookiePath = "/api/auth/google";

    private readonly IDataProtector protector = dataProtection.CreateProtector(ProtectorPurpose);

    public OAuthPendingFlow Issue(HttpContext context)
    {
        var lifetime = options.Value.StateLifetime;
        var flow = new OAuthPendingFlow(
            GoogleAuthorizationRequest.NewRandomValue(),
            GoogleAuthorizationRequest.NewRandomValue(),
            time.GetUtcNow().Add(lifetime));
        var payload = protector.Protect(JsonSerializer.Serialize(flow));
        context.Response.Cookies.Append(CookieName, payload, CookieOptions(context, lifetime));
        return flow;
    }

    /// <summary>
    /// Reads and always deletes the cookie (one use only). Returns the flow only when the cookie decrypts, has not
    /// expired and its state matches <paramref name="state"/>.
    /// </summary>
    public OAuthPendingFlow? Consume(HttpContext context, string? state)
    {
        var raw = context.Request.Cookies[CookieName];
        context.Response.Cookies.Delete(CookieName, CookieOptions(context, TimeSpan.Zero));
        if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(state))
        {
            return null;
        }

        OAuthPendingFlow? flow;
        try
        {
            flow = JsonSerializer.Deserialize<OAuthPendingFlow>(protector.Unprotect(raw));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }

        if (flow is null || flow.ExpiresAt <= time.GetUtcNow() || !FixedTimeEquals(flow.State, state))
        {
            return null;
        }

        return flow;
    }

    private static bool FixedTimeEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    private static CookieOptions CookieOptions(HttpContext context, TimeSpan maxAge) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps,
        Path = CookiePath,
        MaxAge = maxAge,
        IsEssential = true,
    };
}
