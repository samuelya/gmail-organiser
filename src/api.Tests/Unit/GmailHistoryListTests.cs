using System.Net;
using System.Text;
using GmailOrganiser.Gmail;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Google.Apis.Services;

namespace GmailOrganiser.Tests.Unit;

/// <summary><see cref="GmailHistoryList"/> against stubbed <c>history.list</c> responses (no network, no Google account).</summary>
public sealed class GmailHistoryListTests : IDisposable
{
    private readonly StubHandler handler = new();
    private readonly GmailService service;

    public GmailHistoryListTests() =>
        service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientFactory = new StubHttpClientFactory(handler),
            ApplicationName = "gmail-organiser-tests",
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None,
        });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => service.Dispose();

    [Fact]
    public async Task A_page_maps_every_history_type_and_requests_all_four()
    {
        Uri? sent = null;
        handler.Respond = request =>
        {
            sent = request.RequestUri;
            return Json(HttpStatusCode.OK, """
                {"history":[
                  {"id":"1001","messagesAdded":[{"message":{"id":"a1","threadId":"a1"}}]},
                  {"id":"1002","messagesDeleted":[{"message":{"id":"d1"}}]},
                  {"id":"1003","labelsAdded":[{"message":{"id":"a1"},"labelIds":["STARRED"]}],
                               "labelsRemoved":[{"message":{"id":"b1"},"labelIds":["INBOX","UNREAD"]}]}],
                 "nextPageToken":"next-1","historyId":"1010"}
                """);
        };

        var page = await GmailHistoryList.SendAsync(service, "1000", "token-0", Ct);

        page.NextPageToken.ShouldBe("next-1");
        page.HistoryId.ShouldBe("1010");
        page.Records.Select(r => r.Id).ShouldBe(["1001", "1002", "1003"]);
        page.Records[0].MessagesAdded.ShouldBe(["a1"]);
        page.Records[1].MessagesDeleted.ShouldBe(["d1"]);
        page.Records[2].LabelsAdded.ShouldHaveSingleItem().ShouldBe(new LabelChange("a1", ["STARRED"]), new LabelChangeComparer());
        page.Records[2].LabelsRemoved.ShouldHaveSingleItem().ShouldBe(new LabelChange("b1", ["INBOX", "UNREAD"]), new LabelChangeComparer());
        var query = Uri.UnescapeDataString(sent.ShouldNotBeNull().Query);
        query.ShouldContain("startHistoryId=1000");
        query.ShouldContain("pageToken=token-0");
        query.ShouldContain($"maxResults={GmailHistoryList.MaxPageSize}");
        foreach (var type in new[] { "messageAdded", "messageDeleted", "labelAdded", "labelRemoved" })
        {
            query.ShouldContain($"historyTypes={type}");
        }
    }

    [Fact]
    public async Task An_empty_page_keeps_the_mailbox_history_id()
    {
        handler.Respond = _ => Json(HttpStatusCode.OK, """{"historyId":"1000"}""");

        var page = await GmailHistoryList.SendAsync(service, "1000", null, Ct);

        page.Records.ShouldBeEmpty();
        page.NextPageToken.ShouldBeNull();
        page.HistoryId.ShouldBe("1000");
    }

    [Fact]
    public async Task A_404_is_expired_history()
    {
        handler.Respond = _ => Error(HttpStatusCode.NotFound, "Requested entity was not found.", "notFound");

        await Should.ThrowAsync<GmailHistoryExpiredException>(() => GmailHistoryList.SendAsync(service, "1000", null, Ct));
    }

    [Fact]
    public async Task A_start_id_that_is_not_a_number_is_an_argument_error_not_expiry()
    {
        handler.Respond = _ => throw new InvalidOperationException("No request expected.");

        await Should.ThrowAsync<ArgumentException>(() => GmailHistoryList.SendAsync(service, "not-a-number", null, Ct));
    }

    [Fact]
    public async Task A_rejected_page_token_is_an_invalid_page_token()
    {
        handler.Respond = _ => Error(HttpStatusCode.BadRequest, "Invalid pageToken", "invalidArgument");

        await Should.ThrowAsync<GmailInvalidPageTokenException>(() => GmailHistoryList.SendAsync(service, "1000", "stale", Ct));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string message, string reason) =>
        Json(status, $$$"""{"error":{"code":{{{(int)status}}},"message":"{{{message}}}","errors":[{"message":"{{{message}}}","domain":"global","reason":"{{{reason}}}"}]}}""");

    private sealed class LabelChangeComparer : IEqualityComparer<LabelChange>
    {
        public bool Equals(LabelChange? x, LabelChange? y) =>
            x is not null && y is not null && x.MessageId == y.MessageId && x.LabelIds.SequenceEqual(y.LabelIds);

        public int GetHashCode(LabelChange obj) => obj.MessageId.GetHashCode(StringComparison.Ordinal);
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
