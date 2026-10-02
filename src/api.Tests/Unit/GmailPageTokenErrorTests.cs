using System.Net;
using System.Text;
using GmailOrganiser.Gmail;
using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Google.Apis.Services;

namespace GmailOrganiser.Tests.Unit;

/// <summary>
/// <see cref="GoogleGmailClient.IsInvalidPageToken"/> on the <see cref="GoogleApiException"/> the real Google.Apis
/// SDK raises for a stubbed <c>messages.list</c> error response (no network, no Google account).
/// </summary>
public sealed class GmailPageTokenErrorTests : IDisposable
{
    private readonly StubHandler handler = new();
    private readonly GmailService service;

    public GmailPageTokenErrorTests() =>
        service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientFactory = new StubHttpClientFactory(handler),
            ApplicationName = "gmail-organiser-tests",
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None,
        });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => service.Dispose();

    [Theory]
    [InlineData("Invalid pageToken", "invalidArgument", null)]
    [InlineData("Invalid page token", "invalid", null)]
    [InlineData("Invalid value", "invalid", "pageToken")]
    public async Task A_400_naming_the_page_token_is_an_invalid_page_token(string message, string reason, string? location)
    {
        var ex = await ListAsync(HttpStatusCode.BadRequest, message, reason, location);

        GoogleGmailClient.IsInvalidPageToken(ex).ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Invalid query", "invalidArgument")]
    [InlineData(HttpStatusCode.BadRequest, "Invalid label: Label_404", "invalidArgument")]
    [InlineData(HttpStatusCode.NotFound, "Invalid pageToken", "notFound")]
    public async Task Other_errors_are_not_reported_as_an_invalid_page_token(HttpStatusCode status, string message, string reason)
    {
        var ex = await ListAsync(status, message, reason, null);

        GoogleGmailClient.IsInvalidPageToken(ex).ShouldBeFalse();
    }

    private async Task<GoogleApiException> ListAsync(HttpStatusCode status, string message, string reason, string? location)
    {
        var where = location is null ? "" : $$""","location":"{{location}}","locationType":"parameter" """;
        var body = $$$"""{"error":{"code":{{{(int)status}}},"message":"{{{message}}}","errors":[{"message":"{{{message}}}","domain":"global","reason":"{{{reason}}}"{{{where}}}}]}}""";
        handler.Respond = _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        var request = service.Users.Messages.List("me");
        request.PageToken = "synthetic-token";

        return await Should.ThrowAsync<GoogleApiException>(() => request.ExecuteAsync(Ct));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotImplemented);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler stub) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => stub;
    }
}
