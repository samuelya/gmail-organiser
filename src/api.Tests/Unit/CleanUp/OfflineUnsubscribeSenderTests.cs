using GmailOrganiser.CleanUp.Unsubscribe;
using GmailOrganiser.Gmail;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Unit.CleanUp;

/// <summary>Fake Gmail mode registers the offline sender, so a dev or demo stack never POSTs to a real host.</summary>
public sealed class OfflineUnsubscribeSenderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, typeof(OfflineUnsubscribeSender))]
    [InlineData(false, typeof(HttpUnsubscribeSender))]
    public void Fake_gmail_mode_picks_the_offline_sender(bool useFake, Type expected)
    {
        CreateSender(useFake).ShouldBeOfType(expected);
    }

    [Theory]
    [InlineData("https://unsubscribe.invalid/0001")]
    [InlineData("https://example.com/unsubscribe/0001")]
    public async Task An_allowed_url_is_done_without_a_request(string url)
    {
        (await CreateSender(useFake: true).SendOneClickAsync(new Uri(url), Ct)).ShouldBe(new UnsubscribeSendResult(true, 200));
    }

    [Theory]
    [InlineData("http://example.com/u")]
    [InlineData("https://127.0.0.1/u")]
    public async Task A_url_the_guard_refuses_fails(string url)
    {
        (await CreateSender(useFake: true).SendOneClickAsync(new Uri(url), Ct)).ShouldBe(new UnsubscribeSendResult(false, null));
    }

    private static IUnsubscribeSender CreateSender(bool useFake)
    {
        var services = new ServiceCollection().AddLogging();
        services.Configure<GmailOptions>(o => o.UseFake = useFake);
        services.AddUnsubscribe();
        return services.BuildServiceProvider().GetRequiredService<IUnsubscribeSender>();
    }
}
