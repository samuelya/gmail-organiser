using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;

namespace GmailOrganiser.Tests.Unit;

public sealed class FakeGmailClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetProfileAsync_returns_the_synthetic_profile()
    {
        var client = new FakeGmailClient(TimeProvider.System);

        var profile = await client.GetProfileAsync(Ct);

        profile.EmailAddress.ShouldBe("user@example.com");
        profile.MessagesTotal.ShouldBe(client.Messages.Count);
        profile.MessagesTotal.ShouldBeGreaterThan(0);
        long.TryParse(profile.HistoryId, out _).ShouldBeTrue();
    }

    [Fact]
    public void Seeded_mailbox_uses_only_example_com_senders()
    {
        var client = new FakeGmailClient(TimeProvider.System);

        client.Messages.Select(m => m.From).Distinct().Count().ShouldBeGreaterThan(1);
        client.Messages.ShouldAllBe(m => m.From.Contains("example.com>"));
        client.Messages.Select(m => m.Id).ShouldBeUnique();
    }

    [Fact]
    public async Task FakeTokenStore_starts_connected_as_the_fake_account_and_can_disconnect()
    {
        var store = new FakeTokenStore(TimeProvider.System);

        var token = await store.GetAsync(Ct);
        token.ShouldNotBeNull();
        token.AccountEmail.ShouldBe(FakeGmailClient.AccountEmail);
        token.Scopes.ShouldBe(GmailScopes.All);

        await store.DeleteAsync(Ct);
        (await store.GetAsync(Ct)).ShouldBeNull();
    }

    [Fact]
    public void Scopes_never_include_full_mail_access()
    {
        GmailScopes.All.ShouldNotContain("https://mail.google.com/");
        GmailScopes.All.Count.ShouldBe(3);
    }
}
