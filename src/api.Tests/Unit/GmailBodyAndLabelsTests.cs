using System.Buffers.Text;
using System.Net;
using System.Text;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit;

/// <summary>Message bodies, labels and <c>batchModify</c>: part walking on hand-built payloads, limits, and the fake.</summary>
public sealed class GmailBodyAndLabelsTests : IDisposable
{
    private const string Secret = "Synthetic secret body text";

    private readonly StubHandler handler = new();
    private readonly GmailService service;

    public GmailBodyAndLabelsTests() =>
        service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientFactory = new StubHttpClientFactory(handler),
            ApplicationName = "gmail-organiser-tests",
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None,
        });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => service.Dispose();

    [Fact]
    public void ReadBody_takes_the_first_text_and_html_parts_depth_first_and_skips_attachments()
    {
        var message = new Message
        {
            Payload = Multipart(
                "multipart/mixed",
                Multipart(
                    "multipart/alternative",
                    Part("text/plain", "First text ✓"),
                    Multipart("multipart/related", Part("text/html", "<p>First html</p>"), Part("image/png", "png"))),
                Part("text/plain", "Second text"),
                Part("text/plain", "Attached text", filename: "notes.txt")),
        };

        var body = GoogleGmailClient.ReadBody(message);

        body.ShouldBe(new GmailMessageBody("First text ✓", "<p>First html</p>"));
    }

    [Fact]
    public void ReadBody_skips_an_attachment_that_comes_first()
    {
        var message = new Message
        {
            Payload = Multipart("multipart/mixed", Part("text/plain", "Attached", filename: "a.txt"), Part("text/plain", "Real")),
        };

        GoogleGmailClient.ReadBody(message).Text.ShouldBe("Real");
    }

    [Fact]
    public void ReadBody_reads_a_single_part_html_message_and_an_empty_payload()
    {
        GoogleGmailClient.ReadBody(new Message { Payload = Part("text/html", "<b>Only html</b>") })
            .ShouldBe(new GmailMessageBody(null, "<b>Only html</b>"));
        GoogleGmailClient.ReadBody(new Message()).ShouldBe(new GmailMessageBody(null, null));
    }

    [Theory]
    [InlineData("text/plain; charset=ISO-8859-1", "iso-8859-1")]
    [InlineData("text/plain; charset=\"windows-1252\"", "windows-1252")]
    [InlineData("text/plain; charset=utf-8", "utf-8")]
    public void ReadBody_decodes_with_the_part_charset(string contentType, string encodingName)
    {
        const string text = "Grüße aus Köln";
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(encodingName) ?? Encoding.GetEncoding(encodingName);
        var part = Part("text/plain", "", contentType);
        part.Body.Data = Base64Url.EncodeToString(encoding.GetBytes(text));

        GoogleGmailClient.ReadBody(new Message { Payload = part }).Text.ShouldBe(text);
    }

    [Fact]
    public void ReadBody_falls_back_to_utf8_for_an_unknown_or_missing_charset()
    {
        GoogleGmailClient.ReadBody(new Message { Payload = Part("text/plain", "Grüße", "text/plain; charset=x-unknown") }).Text.ShouldBe("Grüße");
        GoogleGmailClient.ReadBody(new Message { Payload = Part("text/plain", "Grüße", contentType: null) }).Text.ShouldBe("Grüße");
    }

    [Fact]
    public async Task GetBodyAsync_reads_the_full_message_and_logs_no_body_text()
    {
        Uri? sent = null;
        handler.Respond = request =>
        {
            sent = request.RequestUri;
            return Json(HttpStatusCode.OK, $$$"""
                {"id":"m1","payload":{"mimeType":"multipart/alternative","parts":[
                  {"mimeType":"text/plain","body":{"data":"{{{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Secret))}}}"}},
                  {"mimeType":"text/html","body":{"data":"{{{Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"<p>{Secret}</p>"))}}}"}}]}}
                """);
        };
        var logger = new CapturingLogger();

        var body = await GoogleGmailClient.GetBodyAsync(service, "m1", logger, Ct);

        body.ShouldBe(new GmailMessageBody(Secret, $"<p>{Secret}</p>"));
        // format=full is the API default, so the client may omit it; metadata or minimal would carry no parts.
        sent.ShouldNotBeNull().AbsolutePath.ShouldEndWith("/messages/m1");
        sent.Query.ShouldNotContain("format=metadata");
        logger.Entries.ShouldNotBeEmpty();
        logger.Entries.ShouldAllBe(e => !e.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetBodyAsync_returns_null_when_gmail_answers_404()
    {
        handler.Respond = _ => Json(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"Requested entity was not found."}}""");

        (await GoogleGmailClient.GetBodyAsync(service, "gone", new CapturingLogger(), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Limits_reject_oversized_or_empty_modifications_and_long_label_names()
    {
        var client = NewFake();
        var tooMany = Enumerable.Range(0, GmailLimits.BatchModifyMaxIds + 1).Select(i => $"id{i}").ToList();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => client.BatchModifyAsync(tooMany, ["INBOX"], [], Ct));
        await Should.ThrowAsync<ArgumentException>(() => client.BatchModifyAsync(["fake-msg-0001"], [], [], Ct));
        await Should.ThrowAsync<ArgumentException>(() => client.CreateLabelAsync(new string('a', GmailLimits.LabelNameMaxLength + 1), Ct));
        await Should.ThrowAsync<ArgumentException>(() => client.CreateLabelAsync(" ", Ct));
    }

    [Fact]
    public void Options_require_the_budget_to_fit_one_batchModify()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gmail:BatchSize"] = "5",
            ["Gmail:QuotaUnitsPerSecond"] = (GmailQuotaLimiter.BatchModifyUnits - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        using var provider = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddGmail().BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GmailOptions>>().Value)
            .Message.ShouldContain("QuotaUnitsPerSecond");
    }

    [Fact]
    public async Task Fake_lists_system_and_nested_user_labels_and_creates_idempotently()
    {
        var client = NewFake();

        var labels = await client.ListLabelsAsync(Ct);
        labels.ShouldContain(new GmailLabel("INBOX", "INBOX", GmailLabelType.System));
        labels.ShouldContain(l => l.Name == "Example/Nested" && l.Type == GmailLabelType.User && l.Id.StartsWith("Label_"));
        labels.Where(l => l.Name.StartsWith("CATEGORY_")).ShouldAllBe(l => l.Type == GmailLabelType.System);

        var created = await client.CreateLabelAsync("Synthetic/New", Ct);
        created.ShouldBe(new GmailLabel($"Label_{FakeLabelStoreUserCount + 1}", "Synthetic/New", GmailLabelType.User));
        (await client.CreateLabelAsync("synthetic/new", Ct)).ShouldBe(created);
        (await client.ListLabelsAsync(Ct)).Count.ShouldBe(labels.Count + 1);
    }

    [Fact]
    public async Task Fake_batchModify_updates_labels_and_records_history_for_changed_messages_only()
    {
        var client = NewFake();
        var label = (await client.ListLabelsAsync(Ct)).First(l => l.Name == "Example");
        var start = (await client.GetProfileAsync(Ct)).HistoryId;
        var inbox = client.Messages.Where(m => m.LabelIds.Contains("INBOX")).Take(2).Select(m => m.Id).ToList();
        var archived = client.Messages.First(m => !m.LabelIds.Contains("INBOX")).Id;

        await client.BatchModifyAsync([.. inbox, archived, "unknown-id"], [label.Id], ["INBOX"], Ct);
        await client.BatchModifyAsync(inbox, [label.Id], ["INBOX"], Ct);

        client.Messages.Where(m => inbox.Contains(m.Id) || m.Id == archived)
            .ShouldAllBe(m => m.LabelIds.Contains(label.Id) && !m.LabelIds.Contains("INBOX"));
        var records = (await client.ListHistoryAsync(start, null, Ct)).Records;
        records.Count.ShouldBe(3);
        records.SelectMany(r => r.LabelsAdded).Select(c => c.MessageId).ShouldBe([.. inbox, archived], ignoreOrder: true);
        records.SelectMany(r => r.LabelsAdded).ShouldAllBe(c => c.LabelIds.SequenceEqual(new[] { label.Id }));
        records.SelectMany(r => r.LabelsRemoved).Select(c => c.MessageId).ShouldBe(inbox, ignoreOrder: true);
    }

    [Fact]
    public async Task Fake_batchModify_rejects_unknown_labels_and_retries_rate_limits()
    {
        var client = NewFake();
        var id = client.Messages[0].Id;

        (await Should.ThrowAsync<GoogleApiException>(() => client.BatchModifyAsync([id], ["Label_999"], [], Ct)))
            .HttpStatusCode.ShouldBe(HttpStatusCode.BadRequest);

        client.FailNext(HttpStatusCode.TooManyRequests, 1);
        await client.BatchModifyAsync([id], ["STARRED"], [], Ct);
        client.Messages[0].LabelIds.ShouldContain("STARRED");
    }

    [Fact]
    public async Task Fake_bodies_mix_html_only_text_only_and_both_and_unknown_ids_are_null()
    {
        var client = NewFake();
        var bodies = new List<GmailMessageBody>();
        foreach (var message in client.Messages.Take(6))
        {
            bodies.Add((await client.GetMessageBodyAsync(message.Id, Ct)).ShouldNotBeNull());
        }

        bodies.ShouldContain(b => b.Text == null && b.Html != null);
        bodies.ShouldContain(b => b.Text != null && b.Html == null);
        bodies.ShouldContain(b => b.Text != null && b.Html != null);
        (await client.GetMessageBodyAsync("unknown-id", Ct)).ShouldBeNull();
    }

    private const int FakeLabelStoreUserCount = 3;

    private static FakeGmailClient NewFake() =>
        new(new FakeTokenStore(TimeProvider.System), FakeMailboxSeed.Create(DateTimeOffset.UnixEpoch.AddYears(56)),
            new GmailRetryPolicy(Options.Create(new GmailOptions()), new ImmediateTimeProvider()));

    private static MessagePart Multipart(string mimeType, params MessagePart[] parts) => new() { MimeType = mimeType, Parts = parts };

    private static MessagePart Part(string mimeType, string content, string? contentType = "", string? filename = null) => new()
    {
        MimeType = mimeType,
        Filename = filename ?? "",
        Headers = contentType is null ? null : [new MessagePartHeader { Name = "Content-Type", Value = contentType.Length > 0 ? contentType : mimeType }],
        Body = new MessagePartBody { Data = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(content)) },
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Backoff delays complete at once, so the retry test needs no fake clock driving.</summary>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                Entries.AddRange(values.Select(v => $"{v.Key}={v.Value}"));
            }
        }
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
