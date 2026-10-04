using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GmailOrganiser.Gmail;
using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Google.Apis.Requests;
using Google.Apis.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Unit;

/// <summary>
/// <see cref="GmailMetadataBatch"/> through the real Google.Apis <see cref="BatchRequest"/>, against a stub HTTP
/// handler that answers with Gmail-shaped multipart batch responses (no network, no Google account).
/// </summary>
public sealed class GmailMetadataBatchTests : IDisposable
{
    private const string Boundary = "batch_test";

    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly StubHandler handler = new();
    private readonly GmailService service;

    public GmailMetadataBatchTests() =>
        service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientFactory = new StubHttpClientFactory(handler),
            ApplicationName = "gmail-organiser-tests",
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None,
        });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => service.Dispose();

    [Fact]
    public async Task The_SDK_reports_a_throttled_batch_call_as_HttpRequestException_wrapping_GoogleApiException()
    {
        handler.Respond = _ => Error(HttpStatusCode.TooManyRequests, "rateLimitExceeded");
        var batch = new BatchRequest(service);
        batch.Queue<Google.Apis.Gmail.v1.Data.Message>(service.Users.Messages.Get("me", "m1"), (_, _, _, _) => { });

        var ex = await Should.ThrowAsync<HttpRequestException>(() => batch.ExecuteAsync(Ct));

        ex.StatusCode.ShouldBeNull();
        GmailMetadataBatch.AsApiException(ex)!.HttpStatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, null)]
    [InlineData(HttpStatusCode.Forbidden, "userRateLimitExceeded")]
    [InlineData(HttpStatusCode.InternalServerError, "backendError")]
    public async Task A_throttled_or_failed_batch_call_returns_every_item_for_retry(HttpStatusCode status, string? reason)
    {
        handler.Respond = _ => Error(status, reason);

        var attempt = await SendAsync(["m1", "m2"], Limiter(budget: 10));

        attempt.Succeeded.ShouldBeEmpty();
        attempt.Retry.ShouldBe(["m1", "m2"]);
    }

    [Fact]
    public async Task A_batch_level_401_surfaces_as_GoogleApiException_with_the_status()
    {
        handler.Respond = _ => Error(HttpStatusCode.Unauthorized, "authError");

        var ex = await Should.ThrowAsync<GoogleApiException>(() => SendAsync(["m1"], Limiter(budget: 10)));

        ex.HttpStatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "backendError")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "backendError")]
    [InlineData(HttpStatusCode.TooManyRequests, "rateLimitExceeded")]
    public async Task Rate_limited_and_transient_items_are_retried_404s_dropped(HttpStatusCode status, string reason)
    {
        handler.Respond = _ => Multipart(
            (HttpStatusCode.OK, MessageJson("m1")),
            (status, ErrorJson(status, reason)),
            (HttpStatusCode.NotFound, ErrorJson(HttpStatusCode.NotFound, "notFound")));

        var attempt = await SendAsync(["m1", "m2", "m3"], Limiter(budget: 15));

        attempt.Succeeded.Select(m => m.Id).ShouldBe(["m1"]);
        attempt.Retry.ShouldBe(["m2"]);
    }

    [Fact]
    public async Task Other_item_errors_throw()
    {
        handler.Respond = _ => Multipart(
            (HttpStatusCode.OK, MessageJson("m1")),
            (HttpStatusCode.BadRequest, ErrorJson(HttpStatusCode.BadRequest, "invalidArgument")));

        var ex = await Should.ThrowAsync<GoogleApiException>(() => SendAsync(["m1", "m2"], Limiter(budget: 10)));

        ex.HttpStatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_whole_batch_cost_is_spent_just_before_it_is_sent()
    {
        handler.Respond = _ => Multipart((HttpStatusCode.OK, MessageJson("m1")), (HttpStatusCode.OK, MessageJson("m2")));
        var limiter = Limiter(budget: 10);
        limiter.TryAcquire(5, out _).ShouldBeTrue();

        var task = SendAsync(["m1", "m2"], limiter);
        await Task.Delay(20, Ct);
        handler.Calls.ShouldBe(0);

        time.Advance(TimeSpan.FromSeconds(1));
        (await task).Succeeded.Count.ShouldBe(2);
        handler.Calls.ShouldBe(1);
        limiter.TryAcquire(5, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_labels_batch_asks_for_format_minimal_and_maps_the_label_ids()
    {
        string? body = null;
        handler.Respond = request =>
        {
            body = request.Content!.ReadAsStringAsync(Ct).GetAwaiter().GetResult();
            return Multipart(
                (HttpStatusCode.OK, """{"id":"m1","threadId":"t-m1","labelIds":["INBOX","UNREAD"]}"""),
                (HttpStatusCode.OK, """{"id":"m2","threadId":"t-m2"}"""),
                (HttpStatusCode.NotFound, ErrorJson(HttpStatusCode.NotFound, "notFound")));
        };

        var attempt = await GmailMetadataBatch.SendLabelsAsync(service, ["m1", "m2", "m3"], Limiter(budget: 15), NullLogger.Instance, Ct);

        body.ShouldNotBeNull().ShouldContain("format=minimal");
        body.ShouldNotContain("metadataHeaders");
        attempt.Succeeded.Select(m => $"{m.Id}:{string.Join(',', m.LabelIds)}").ShouldBe(["m1:INBOX,UNREAD", "m2:"]);
        attempt.Retry.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_label_totals_batch_gets_each_label_at_one_unit_and_drops_deleted_labels()
    {
        string? body = null;
        handler.Respond = request =>
        {
            body = request.Content!.ReadAsStringAsync(Ct).GetAwaiter().GetResult();
            return Multipart(
                (HttpStatusCode.OK, """{"id":"Label_1","name":"Synthetic","messagesTotal":7}"""),
                (HttpStatusCode.OK, """{"id":"Label_2","name":"Synthetic Empty"}"""),
                (HttpStatusCode.NotFound, ErrorJson(HttpStatusCode.NotFound, "notFound")));
        };

        var attempt = await GmailMetadataBatch.SendLabelTotalsAsync(
            service, ["Label_1", "Label_2", "Label_3"], Limiter(budget: 3), NullLogger.Instance, Ct);

        body.ShouldNotBeNull().ShouldContain("/labels/Label_1");
        attempt.Succeeded.ShouldBe([new GmailLabelTotal("Label_1", 7), new GmailLabelTotal("Label_2", 0)]);
        attempt.Retry.ShouldBeEmpty();
    }

    private Task<GmailBatchAttempt<string, GmailMessageMetadata>> SendAsync(IReadOnlyList<string> ids, GmailQuotaLimiter limiter) =>
        GmailMetadataBatch.SendAsync(service, ids, limiter, NullLogger.Instance, Ct);

    private GmailQuotaLimiter Limiter(int budget) =>
        new(Options.Create(new GmailOptions { QuotaUnitsPerSecond = budget }), time);

    private static string MessageJson(string id) =>
        $$$"""{"id":"{{{id}}}","threadId":"t-{{{id}}}","payload":{"mimeType":"text/plain","headers":[{"name":"From","value":"Sender <sender@example.com>"}]}}""";

    private static string ErrorJson(HttpStatusCode status, string? reason)
    {
        var errors = reason is null ? "" : $$"""{"reason":"{{reason}}","message":"synthetic"}""";
        return $$$"""{"error":{"code":{{{(int)status}}},"message":"synthetic","errors":[{{{errors}}}]}}""";
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string? reason) =>
        new(status) { Content = new StringContent(ErrorJson(status, reason), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Multipart(params (HttpStatusCode Status, string Body)[] parts)
    {
        var body = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            body.Append(CultureInfo.InvariantCulture, $"--{Boundary}\r\nContent-Type: application/http\r\nContent-ID: <response-{i + 1}>\r\n\r\n")
                .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {(int)parts[i].Status} Synthetic\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n")
                .Append(parts[i].Body).Append("\r\n");
        }

        body.Append(CultureInfo.InvariantCulture, $"--{Boundary}--\r\n");
        var content = new StringContent(body.ToString(), Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed; boundary={Boundary}");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotImplemented);

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler stub) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => stub;
    }
}
