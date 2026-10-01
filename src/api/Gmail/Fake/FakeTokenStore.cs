namespace GmailOrganiser.Gmail.Fake;

/// <summary>
/// In-memory token store for <c>GMAIL_FAKE=true</c>: starts connected as the fake mailbox's account.
/// Nothing is written to the database.
/// </summary>
public sealed class FakeTokenStore(TimeProvider time) : ITokenStore
{
    public const string FakeRefreshToken = "fake-refresh-token";

    private readonly Lock gate = new();
    private OAuthToken? token = new(
        FakeGmailClient.AccountEmail, FakeRefreshToken, GmailScopes.All, time.GetUtcNow(), time.GetUtcNow());

    public Task<OAuthToken?> GetAsync(CancellationToken ct = default)
    {
        lock (gate)
        {
            return Task.FromResult(token);
        }
    }

    public Task SaveAsync(string accountEmail, string refreshToken, IReadOnlyList<string> scopes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        var now = time.GetUtcNow();
        lock (gate)
        {
            var connectedAt = token?.AccountEmail == accountEmail ? token.ConnectedAt : now;
            token = new OAuthToken(accountEmail, refreshToken, scopes.ToArray(), connectedAt, now);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken ct = default)
    {
        lock (gate)
        {
            token = null;
        }

        return Task.CompletedTask;
    }
}
