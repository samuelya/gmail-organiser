using GmailOrganiser.Common;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Auth;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit;

public sealed class GmailConnectorTests
{
    private static readonly TimeSpan RevokeTimeout = TimeSpan.FromMilliseconds(300);

    private readonly StubGoogleOAuthClient oauth = new() { RevokeHangs = true };
    private readonly FakeTokenStore tokens = new(TimeProvider.System);
    private readonly StubAccountGuard accountGuard = new();

    [Fact]
    public async Task Disconnect_deletes_the_token_when_the_request_is_aborted_while_revoke_hangs()
    {
        await tokens.SaveAsync("user@example.com", "synthetic-refresh-token", GmailScopes.All, TestContext.Current.CancellationToken);
        using var request = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await CreateConnector().DisconnectAsync(request.Token);

        request.IsCancellationRequested.ShouldBeTrue();
        oauth.RevokedTokens.ShouldBe(["synthetic-refresh-token"]);
        oauth.RevokeToken.IsCancellationRequested.ShouldBeTrue("revoke must end on its own timeout");
        (await tokens.GetAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Disconnect_gives_up_on_a_hanging_revoke_after_its_own_timeout()
    {
        await tokens.SaveAsync("user@example.com", "synthetic-refresh-token", GmailScopes.All, TestContext.Current.CancellationToken);

        var disconnect = CreateConnector().DisconnectAsync(CancellationToken.None);

        (await Task.WhenAny(disconnect, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))).ShouldBe(disconnect);
        await disconnect;
        (await tokens.GetAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Connect_keeps_the_current_account_when_the_guard_refuses_a_switch_during_a_fetch()
    {
        await tokens.SaveAsync("user@example.com", "synthetic-refresh-token", GmailScopes.All, TestContext.Current.CancellationToken);
        oauth.AccountEmail = "other@example.com";
        accountGuard.RefusesConnect = true;

        var outcome = await CreateConnector(useFake: true).CompleteAsync("synthetic-code", null, "synthetic-verifier", TestContext.Current.CancellationToken);

        outcome.ShouldBe(ConnectOutcome.FetchActive);
        outcome.ToReason().ShouldBe("fetch_active");
        (await tokens.GetAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull().AccountEmail.ShouldBe("user@example.com");
    }

    private GmailConnector CreateConnector(bool useFake = false) => new(
        oauth,
        tokens,
        accountGuard,
        new GoogleClientService(
            new InMemorySettingsStore(),
            new EphemeralDataProtectionProvider(),
            Options.Create(new SettingsEnvOptions()),
            NullLogger<GoogleClientService>.Instance),
        Options.Create(new AppOptions()),
        Options.Create(new GmailOptions { UseFake = useFake }),
        Options.Create(new GoogleOAuthOptions { RevokeTimeout = RevokeTimeout }),
        TimeProvider.System,
        NullLogger<GmailConnector>.Instance);
}
