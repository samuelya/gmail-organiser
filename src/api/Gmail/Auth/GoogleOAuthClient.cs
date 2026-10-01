using System.Text.Json;
using System.Text.Json.Serialization;
using GmailOrganiser.Settings;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Gmail.Auth;

/// <summary>
/// <see cref="IGoogleOAuthClient"/> over plain HTTP to Google's token and revoke endpoints (keeps PKCE explicit).
/// Never logs codes, tokens or the client secret.
/// </summary>
public sealed class GoogleOAuthClient(HttpClient http, IOptions<GoogleOAuthOptions> options) : IGoogleOAuthClient
{
    public const string HttpClientName = "google-oauth";

    public async Task<GoogleTokenResult> ExchangeCodeAsync(GoogleCodeExchange exchange, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = exchange.Code,
            ["code_verifier"] = exchange.CodeVerifier,
            ["redirect_uri"] = exchange.RedirectUri,
            ["client_id"] = exchange.ClientId,
            ["client_secret"] = exchange.ClientSecret,
        });

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(options.Value.TokenEndpoint, content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GoogleOAuthException("The Google token endpoint could not be reached.", inner: ex);
        }

        using (response)
        {
            TokenResponseBody? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<TokenResponseBody>(ct);
            }
            catch (JsonException ex)
            {
                throw new GoogleOAuthException($"The Google token endpoint returned an unreadable response ({(int)response.StatusCode}).", inner: ex);
            }

            if (!response.IsSuccessStatusCode || body?.AccessToken is not { Length: > 0 } accessToken)
            {
                throw new GoogleOAuthException(
                    $"The Google token endpoint refused the code ({(int)response.StatusCode}).", SafeErrorCode(body?.Error));
            }

            var scopes = (body.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new GoogleTokenResult(accessToken, string.IsNullOrWhiteSpace(body.RefreshToken) ? null : body.RefreshToken, scopes);
        }
    }

    public async Task<string> GetAccountEmailAsync(string accessToken, CancellationToken ct)
    {
        using var service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = GoogleCredential.FromAccessToken(accessToken),
            ApplicationName = SettingsEndpoints.ApplicationName,
        });
        var profile = await service.Users.GetProfile("me").ExecuteAsync(ct);
        return profile.EmailAddress is { Length: > 0 } email
            ? email
            : throw new GoogleOAuthException("The Gmail profile has no email address.");
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken });
        using var response = await http.PostAsync(options.Value.RevokeEndpoint, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new GoogleOAuthException($"Google refused to revoke the token ({(int)response.StatusCode}).");
        }
    }

    /// <summary>Google error codes are short identifiers; anything else is dropped rather than logged.</summary>
    private static string? SafeErrorCode(string? error) =>
        error is { Length: > 0 and <= 64 } && error.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? error : null;

    private sealed class TokenResponseBody
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
