using GmailOrganiser.CleanUp.Unsubscribe;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Unit.CleanUp;

/// <summary>The real sender as the app registers it; every target here is refused before any connection is made.</summary>
public sealed class HttpUnsubscribeSenderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("http://example.com/u")]
    [InlineData("https://example.com:8443/u")]
    [InlineData("https://127.0.0.1/u")]
    [InlineData("https://user:secret@example.com/u")]
    public async Task Urls_the_guard_refuses_fail_without_a_request(string url)
    {
        (await CreateSender().SendOneClickAsync(new Uri(url), Ct)).ShouldBe(new UnsubscribeSendResult(false, null));
    }

    [Fact]
    public async Task A_name_resolving_to_loopback_is_refused_in_the_connect_callback()
    {
        (await CreateSender().SendOneClickAsync(new Uri("https://localhost/u"), Ct)).ShouldBe(new UnsubscribeSendResult(false, null));
    }

    [Fact]
    public void The_handler_follows_no_redirects_and_keeps_no_cookies_or_proxy()
    {
        using var handler = UnsubscribeHttp.CreateHandler();

        (handler.AllowAutoRedirect, handler.UseCookies, handler.UseProxy, handler.Credentials).ShouldBe((false, false, false, null));
        handler.ConnectCallback.ShouldNotBeNull();
        UnsubscribeHttp.Timeout.ShouldBe(TimeSpan.FromSeconds(15));
    }

    private static IUnsubscribeSender CreateSender()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddUnsubscribe();
        return services.BuildServiceProvider().GetRequiredService<IUnsubscribeSender>();
    }
}
