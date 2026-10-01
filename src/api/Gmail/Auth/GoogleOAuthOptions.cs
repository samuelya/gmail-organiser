namespace GmailOrganiser.Gmail.Auth;

/// <summary>Google's OAuth 2.0 endpoints, bound from <c>GoogleOAuth:*</c>. The defaults are Google's published endpoints.</summary>
public sealed class GoogleOAuthOptions
{
    public const string SectionName = "GoogleOAuth";

    public string AuthorizationEndpoint { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";
    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";
    public string RevokeEndpoint { get; set; } = "https://oauth2.googleapis.com/revoke";

    /// <summary>How long a started connect flow (state + PKCE verifier) stays valid.</summary>
    public TimeSpan StateLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long disconnect waits for Google's best-effort revoke before giving up.</summary>
    public TimeSpan RevokeTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
