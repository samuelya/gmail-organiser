using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Tests.Fakes;

public sealed class StubAccountGuard : IAccountGuard
{
    public AccountCheck Result { get; set; } = new(AccountCheckStatus.Ok);
    public bool RefusesConnect { get; set; }

    public Task<AccountCheck> CheckAsync(CancellationToken ct = default) => Task.FromResult(Result);

    public Task<AccountCheck> CheckAsync(OAuthToken? connected, CancellationToken ct = default) => Task.FromResult(Result);

    public Task<bool> RefusesConnectAsync(string accountEmail, CancellationToken ct = default) => Task.FromResult(RefusesConnect);
}
