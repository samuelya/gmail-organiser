using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Auth;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>Scriptable <see cref="IGoogleOAuthClient"/>: no network, records what it was asked.</summary>
public sealed class StubGoogleOAuthClient : IGoogleOAuthClient
{
    public GoogleTokenResult Result { get; set; } = new("synthetic-access-token", "synthetic-refresh-token", GmailScopes.All);
    public Exception? ExchangeException { get; set; }
    public string AccountEmail { get; set; } = "user@example.com";
    public Exception? RevokeException { get; set; }

    public List<GoogleCodeExchange> Exchanges { get; } = [];
    public List<string> RevokedTokens { get; } = [];

    public Task<GoogleTokenResult> ExchangeCodeAsync(GoogleCodeExchange exchange, CancellationToken ct)
    {
        Exchanges.Add(exchange);
        return ExchangeException is null ? Task.FromResult(Result) : Task.FromException<GoogleTokenResult>(ExchangeException);
    }

    public Task<string> GetAccountEmailAsync(string accessToken, CancellationToken ct) => Task.FromResult(AccountEmail);

    public Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        RevokedTokens.Add(refreshToken);
        return RevokeException is null ? Task.CompletedTask : Task.FromException(RevokeException);
    }
}
