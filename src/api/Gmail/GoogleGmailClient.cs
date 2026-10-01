using System.Globalization;
using GmailOrganiser.Settings;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;

namespace GmailOrganiser.Gmail;

/// <summary>
/// <see cref="IGmailClient"/> over <c>Google.Apis.Gmail.v1</c>. Credentials come from the stored refresh token and
/// the Google client in settings; the SDK refreshes access tokens in memory and never persists them.
/// </summary>
public sealed class GoogleGmailClient(ITokenStore tokens, GoogleClientService googleClient) : IGmailClient
{
    private const string Me = "me";

    public async Task<GmailProfile> GetProfileAsync(CancellationToken ct)
    {
        var (flow, service) = await CreateServiceAsync(ct);
        using (flow)
        using (service)
        {
            try
            {
                var profile = await service.Users.GetProfile(Me).ExecuteAsync(ct);
                return new GmailProfile(
                    profile.EmailAddress ?? "",
                    profile.MessagesTotal ?? 0,
                    profile.HistoryId?.ToString(CultureInfo.InvariantCulture) ?? "");
            }
            catch (TokenResponseException ex) when (ex.Error?.Error == "invalid_grant")
            {
                // Revoked, or expired (a consent screen in Testing mode issues 7-day refresh tokens).
                throw new GmailNotConnectedException("The Gmail connection was revoked or has expired; reconnect Gmail.", ex);
            }
        }
    }

    private async Task<(GoogleAuthorizationCodeFlow Flow, GmailService Service)> CreateServiceAsync(CancellationToken ct)
    {
        var token = await tokens.GetAsync(ct)
            ?? throw new GmailNotConnectedException("Gmail is not connected.");
        var client = await googleClient.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(client.ClientId) || string.IsNullOrWhiteSpace(client.ClientSecret))
        {
            throw new GmailNotConnectedException("The Google OAuth client ID and secret are not configured.");
        }

        // No DataStore: the flow keeps the access token in memory only.
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = client.ClientId, ClientSecret = client.ClientSecret },
            Scopes = GmailScopes.All,
        });
        var credential = new UserCredential(flow, Me, new TokenResponse { RefreshToken = token.RefreshToken });
        var service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = SettingsEndpoints.ApplicationName,
        });
        return (flow, service);
    }
}
