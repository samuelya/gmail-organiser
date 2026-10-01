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

    /// <summary>When set, revoke never answers and only ends when its cancellation token fires.</summary>
    public bool RevokeHangs { get; set; }

    /// <summary>The cancellation token revoke was called with, to check it is not the caller's.</summary>
    public CancellationToken RevokeToken { get; private set; }

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
        RevokeToken = ct;
        if (RevokeHangs)
        {
            return Task.Delay(Timeout.Infinite, ct);
        }

        return RevokeException is null ? Task.CompletedTask : Task.FromException(RevokeException);
    }
}
