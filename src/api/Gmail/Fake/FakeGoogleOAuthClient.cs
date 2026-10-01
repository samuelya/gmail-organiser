using GmailOrganiser.Gmail.Auth;

namespace GmailOrganiser.Gmail.Fake;

/// <summary>
/// <see cref="IGoogleOAuthClient"/> for <c>GMAIL_FAKE=true</c>: accepts <see cref="FakeCode"/>, grants all scopes for
/// <see cref="FakeGmailClient.AccountEmail"/>, and revokes nothing. No network.
/// </summary>
public sealed class FakeGoogleOAuthClient : IGoogleOAuthClient
{
    public const string FakeCode = "fake-authorization-code";
    private const string FakeAccessToken = "fake-access-token";

    public Task<GoogleTokenResult> ExchangeCodeAsync(GoogleCodeExchange exchange, CancellationToken ct) =>
        exchange.Code == FakeCode
            ? Task.FromResult(new GoogleTokenResult(FakeAccessToken, FakeTokenStore.FakeRefreshToken, GmailScopes.All))
            : throw new GoogleOAuthException("Unknown fake authorization code.", "invalid_grant");

    public Task<string> GetAccountEmailAsync(string accessToken, CancellationToken ct) =>
        Task.FromResult(FakeGmailClient.AccountEmail);

    public Task RevokeAsync(string refreshToken, CancellationToken ct) => Task.CompletedTask;
}
