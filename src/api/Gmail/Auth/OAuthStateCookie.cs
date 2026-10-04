using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Gmail.Auth;

/// <summary>
/// Where the callback sends the browser back to. Only these values exist, so the redirect is never built from
/// request input. <see cref="Setup"/> stays 0: a cookie issued before the field existed reads as Setup.
/// </summary>
public enum OAuthReturnTo
{
    Setup = 0,
    Settings = 1,
}

/// <summary>
/// The pending connect flow: the <c>state</c> sent to Google, the PKCE verifier, when the flow expires and the page
/// the callback returns to.
/// </summary>
public sealed record OAuthPendingFlow(string State, string CodeVerifier, DateTimeOffset ExpiresAt, OAuthReturnTo ReturnTo);

/// <param name="Flow">The flow, or null when the cookie is missing, undecryptable, expired or its state differs.</param>
/// <param name="ReturnTo">The cookie's return target whenever it decrypts (even if the flow is invalid), else Setup.</param>
public sealed record OAuthConsumeResult(OAuthPendingFlow? Flow, OAuthReturnTo ReturnTo);

/// <summary>
/// Keeps the pending flow in one short-lived, HttpOnly, SameSite=Lax cookie encrypted with Data Protection.
/// Lax is enough: Google's return to the callback is a top-level GET navigation. The return target rides in the same
/// encrypted payload, so it can't be changed between start and callback.
/// </summary>
public sealed class OAuthStateCookie(IDataProtectionProvider dataProtection, TimeProvider time, IOptions<GoogleOAuthOptions> options)
{
    public const string CookieName = "gmo_google_oauth";
    public const string ProtectorPurpose = "GoogleOAuthState";
    private const string CookiePath = "/api/auth/google";

    private readonly IDataProtector protector = dataProtection.CreateProtector(ProtectorPurpose);

    public OAuthPendingFlow Issue(HttpContext context, OAuthReturnTo returnTo)
    {
        var lifetime = options.Value.StateLifetime;
        var flow = new OAuthPendingFlow(
            GoogleAuthorizationRequest.NewRandomValue(),
            GoogleAuthorizationRequest.NewRandomValue(),
            time.GetUtcNow().Add(lifetime),
            returnTo);
        var payload = protector.Protect(JsonSerializer.Serialize(flow));
        context.Response.Cookies.Append(CookieName, payload, CookieOptions(context, lifetime));
        return flow;
    }

    /// <summary>
    /// Reads and always deletes the cookie (one use only). Returns the flow only when the cookie decrypts, has not
    /// expired and its state matches <paramref name="state"/>; the return target whenever the cookie decrypts.
    /// </summary>
    public OAuthConsumeResult Consume(HttpContext context, string? state)
    {
        var raw = context.Request.Cookies[CookieName];
        context.Response.Cookies.Delete(CookieName, CookieOptions(context, TimeSpan.Zero));
        if (string.IsNullOrEmpty(raw))
        {
            return new OAuthConsumeResult(null, OAuthReturnTo.Setup);
        }

        OAuthPendingFlow? flow;
        try
        {
            flow = JsonSerializer.Deserialize<OAuthPendingFlow>(protector.Unprotect(raw));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return new OAuthConsumeResult(null, OAuthReturnTo.Setup);
        }

        if (flow is null)
        {
            return new OAuthConsumeResult(null, OAuthReturnTo.Setup);
        }

        // An out-of-range number in the payload can't come from Issue; treat it like any unknown target.
        var returnTo = Enum.IsDefined(flow.ReturnTo) ? flow.ReturnTo : OAuthReturnTo.Setup;
        var valid = !string.IsNullOrEmpty(state) && flow.ExpiresAt > time.GetUtcNow() && FixedTimeEquals(flow.State, state);
        return new OAuthConsumeResult(valid ? flow with { ReturnTo = returnTo } : null, returnTo);
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
