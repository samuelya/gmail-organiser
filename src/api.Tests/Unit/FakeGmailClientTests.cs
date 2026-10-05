using System.Net;
using System.Text;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Senders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Unit;

public sealed class FakeGmailClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetProfileAsync_returns_the_synthetic_profile_when_connected()
    {
        var client = new FakeGmailClient(new FakeTokenStore(TimeProvider.System), TimeProvider.System);

        var profile = await client.GetProfileAsync(Ct);

        profile.EmailAddress.ShouldBe("user@example.com");
        profile.MessagesTotal.ShouldBe(client.Messages.Count);
        profile.MessagesTotal.ShouldBeGreaterThan(0);
        long.TryParse(profile.HistoryId, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task GetProfileAsync_throws_not_connected_after_the_token_is_deleted()
    {
        var store = new FakeTokenStore(TimeProvider.System);
        var client = new FakeGmailClient(store, TimeProvider.System);
        await store.DeleteAsync(Ct);

        await Should.ThrowAsync<GmailNotConnectedException>(() => client.GetProfileAsync(Ct));
    }

    [Fact]
    public async Task GetProfileAsync_reports_the_reconnected_account()
    {
        var store = new FakeTokenStore(TimeProvider.System);
        var client = new FakeGmailClient(store, TimeProvider.System);
        await store.DeleteAsync(Ct);
        await store.SaveAsync("other@example.com", "fake-token-2", GmailScopes.All, Ct);

        (await client.GetProfileAsync(Ct)).EmailAddress.ShouldBe("other@example.com");
    }

    [Fact]
    public void Seeded_mailbox_uses_only_example_com_senders()
    {
        var client = new FakeGmailClient(new FakeTokenStore(TimeProvider.System), TimeProvider.System);

        client.Messages.Select(m => m.From).Distinct().Count().ShouldBeGreaterThan(1);
        // The relay addresses stand for an example.com sender.
        client.Messages.ShouldAllBe(m => RelayAddressDecoder.Decode(GmailMetadataMapper.ParseFrom(m.From).Address).CanonicalDomain.EndsWith("example.com"));
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

    [Fact]
    public void Seeded_mailbox_has_the_documented_shape()
    {
        var messages = FakeGmailClient.Seed(Now);

        messages.Count.ShouldBe(FakeMailboxSeed.MessageCount);
        messages.Where(m => !m.LabelIds.Contains("SENT")).Select(m => GmailMetadataMapper.ParseFrom(m.From).Address).Distinct().Count().ShouldBe(8 + FakeMailboxSeed.RelayAddresses.Length);
        messages.Where(m => m.ThreadId == FakeMailboxSeed.RepliedThreadId).Select(m => m.LabelIds.Contains("SENT")).ShouldBe([false, true], ignoreOrder: true);
        messages.ShouldContain(m => m.LabelIds.SequenceEqual(new[] { "INBOX" }));
        messages.ShouldContain(m => !m.LabelIds.Contains("INBOX"));
        messages.ShouldContain(m => m.ListUnsubscribe != null);
        messages.Count(m => m.HasAttachment).ShouldBeGreaterThanOrEqualTo(6);
        messages.ShouldContain(m => m.Attachments.Count == 2);
        messages.SelectMany(m => m.Attachments).ShouldAllBe(a => a.Size >= 1024 && a.Size <= 50 * 1024 && a.MimeType == "application/pdf");
        messages.SelectMany(m => m.Attachments).Select(a => a.Size).Distinct().Count().ShouldBeGreaterThan(5);
        FakeGmailClient.Seed(Now).Select(m => m.TotalSizeEstimate).ShouldBe(messages.Select(m => m.TotalSizeEstimate));
    }

    [Fact]
    public void SyntheticPdf_is_a_minimal_pdf_padded_to_the_requested_size()
    {
        var pdf = SyntheticPdf.Create("Invoice 0001 \u2014 Example (Billing)", "Synthetic invoice for example.com.", minimumSize: 4000);
        var text = Encoding.ASCII.GetString(pdf);

        text.ShouldStartWith("%PDF-");
        text.ShouldEndWith("%%EOF");
        pdf.Length.ShouldBeGreaterThanOrEqualTo(4000);
        text.ShouldContain("/Type /Page ");
        text.ShouldContain("(Invoice 0001 ? Example \\(Billing\\)) Tj");

        // startxref points at the xref table.
        var startxref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        text[startxref..].ShouldStartWith("xref\n0 6\n");
        SyntheticPdf.Create("t", "b").Length.ShouldBeLessThan(1024);
    }

    [Fact]
    public async Task ListMessageIdsAsync_pages_with_opaque_tokens_newest_first()
    {
        var client = Client();
        var seen = new List<string>();
        string? token = null;
        var pages = 0;
        do
        {
            var page = await client.ListMessageIdsAsync(new MessageListQuery(null, null, token, 25), Ct);
            page.ResultSizeEstimate.ShouldBe(FakeMailboxSeed.MessageCount);
            page.Messages.Count.ShouldBeLessThanOrEqualTo(25);
            seen.AddRange(page.Messages.Select(m => m.Id));
            token = page.NextPageToken;
            token?.ShouldNotContain("25");
            pages++;
        }
        while (token is not null);

        pages.ShouldBe(3);
        seen.ShouldBe(client.Messages.OrderByDescending(m => m.Date).Select(m => m.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task ListMessageIdsAsync_rejects_out_of_range_page_sizes(int maxResults) =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Client().ListMessageIdsAsync(new MessageListQuery(null, null, null, maxResults), Ct));

    [Fact]
    public async Task ListMessageIdsAsync_rejects_a_foreign_page_token() =>
        await Should.ThrowAsync<GmailInvalidPageTokenException>(() => Client().ListMessageIdsAsync(new MessageListQuery(null, null, "not-a-token", 10), Ct));

    [Theory]
    [InlineData("from:alice@example.com")]
    [InlineData("from:OFFERS@shop.example.com")]
    [InlineData("from:@travel.example.com")]
    [InlineData("from:@example.com")]
    [InlineData("in:inbox")]
    [InlineData("label:CATEGORY_PROMOTIONS")]
    [InlineData("has:attachment")]
    [InlineData("in:inbox has:attachment")]
    public async Task ListMessageIdsAsync_filters_by_query(string query)
    {
        var client = Client();
        var expected = client.Messages.Where(FakeGmailQuery.Parse(query)).Select(m => m.Id).ToList();

        var page = await client.ListMessageIdsAsync(new MessageListQuery(query, null, null, 500), Ct);

        expected.ShouldNotBeEmpty();
        page.Messages.Select(m => m.Id).ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public void FakeGmailQuery_matches_the_documented_terms()
    {
        var messages = FakeGmailClient.Seed(Now);

        messages.Where(FakeGmailQuery.Parse("from:@example.com")).Count().ShouldBe(messages.Count - FakeMailboxSeed.RelayAddresses.Length);
        messages.Where(FakeGmailQuery.Parse("from:@travel.example.com")).ShouldAllBe(m => m.From.Contains("travel.example.com"));
        messages.Where(FakeGmailQuery.Parse("has:attachment")).ShouldAllBe(m => m.HasAttachment);
        messages.Where(FakeGmailQuery.Parse("in:inbox")).ShouldAllBe(m => m.LabelIds.Contains("INBOX"));
        messages.Where(FakeGmailQuery.Parse("from:offers@shop.example.com")).Count().ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("is:unread")]
    [InlineData("hello")]
    [InlineData("from:")]
    public async Task ListMessageIdsAsync_rejects_unknown_search_terms(string query) =>
        await Should.ThrowAsync<ArgumentException>(() => Client().ListMessageIdsAsync(new MessageListQuery(query, null, null, 10), Ct));

    [Fact]
    public async Task ListMessageIdsAsync_filters_by_label_ids()
    {
        var client = Client();

        var page = await client.ListMessageIdsAsync(new MessageListQuery(null, ["INBOX", "CATEGORY_UPDATES"], null, 500), Ct);

        page.Messages.ShouldNotBeEmpty();
        var byId = client.Messages.ToDictionary(m => m.Id);
        page.Messages.ShouldAllBe(m => byId[m.Id].LabelIds.Contains("INBOX") && byId[m.Id].LabelIds.Contains("CATEGORY_UPDATES"));
    }

    [Theory]
    [InlineData(null, null, "a")]
    [InlineData("SPAM", null, "s")]
    [InlineData("TRASH", null, "t")]
    [InlineData(null, "in:spam", "s")]
    [InlineData(null, "in:trash", "t")]
    [InlineData(null, "label:TRASH", "t")]
    public async Task ListMessageIdsAsync_skips_spam_and_trash_unless_the_request_names_them(
        string? labelId, string? query, string expected)
    {
        var client = new FakeGmailClient(new FakeTokenStore(TimeProvider.System),
        [
            new FakeMessage("a", "a", "a@example.com", "Synthetic", Now, ["INBOX"]),
            new FakeMessage("s", "s", "s@example.com", "Synthetic", Now, ["SPAM"]),
            new FakeMessage("t", "t", "t@example.com", "Synthetic", Now, ["TRASH"]),
        ]);

        var page = await client.ListMessageIdsAsync(new MessageListQuery(query, labelId is null ? null : [labelId], null, 10), Ct);

        page.Messages.Select(m => m.Id).ShouldBe([expected]);
    }

    [Fact]
    public async Task GetMessagesMetadataAsync_returns_request_order_and_omits_unknown_ids()
    {
        var client = Client();

        var metadata = await client.GetMessagesMetadataAsync(["fake-msg-0003", "missing", "fake-msg-0001", "fake-msg-0003"], Ct);

        metadata.Select(m => m.Id).ShouldBe(["fake-msg-0003", "fake-msg-0001"]);
        var source = client.Messages.Single(m => m.Id == "fake-msg-0001");
        metadata[1].From.ShouldBe(source.From);
        metadata[1].Subject.ShouldBe(source.Subject);
        metadata[1].ListId.ShouldBe("weekly.news.example.com");
        metadata[1].ListUnsubscribe.ShouldBe(source.ListUnsubscribe);
        (metadata[1].Precedence, metadata[1].AutoSubmitted).ShouldBe(("bulk", "auto-generated"));
        (metadata[0].Precedence, metadata[0].AutoSubmitted).ShouldBe((null, null));
        metadata[1].InternalDate.ShouldBe(source.Date);
        metadata[1].HistoryId.ShouldBe(source.HistoryId);
    }

    [Fact]
    public async Task GetMessagesMetadataAsync_reports_attachments_with_the_summed_size()
    {
        var client = Client();
        var withPdf = client.Messages.First(m => m.Attachments.Count == 2);
        var without = client.Messages.First(m => !m.HasAttachment);

        var metadata = await client.GetMessagesMetadataAsync([withPdf.Id, without.Id], Ct);

        metadata[0].HasAttachment.ShouldBeTrue();
        metadata[0].SizeEstimate.ShouldBe(withPdf.SizeEstimate + withPdf.Attachments.Sum(a => a.Content.Length));
        metadata[1].HasAttachment.ShouldBeFalse();
        metadata[1].SizeEstimate.ShouldBe(without.SizeEstimate);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetMessagesMetadataAsync_retries_rate_limited_and_transient_items(HttpStatusCode status)
    {
        var time = new FakeTimeProvider(Now);
        var client = Client(time, maxAttempts: 8);
        client.FailNext(status, 2);

        var task = client.GetMessagesMetadataAsync(["fake-msg-0001", "fake-msg-0002", "fake-msg-0003"], Ct);
        var metadata = await DriveAsync(time, task);

        metadata.Select(m => m.Id).ShouldBe(["fake-msg-0001", "fake-msg-0002", "fake-msg-0003"]);
        time.GetUtcNow().ShouldBeGreaterThan(Now);
    }

    [Fact]
    public async Task ListMessageIdsAsync_gives_up_with_GmailRateLimitedException()
    {
        var time = new FakeTimeProvider(Now);
        var client = Client(time, maxAttempts: 3);
        client.FailNext(HttpStatusCode.TooManyRequests, 3);

        var task = client.ListMessageIdsAsync(new MessageListQuery(null, null, null, 10), Ct);

        await Should.ThrowAsync<GmailRateLimitedException>(() => DriveAsync(time, task));
    }

    [Fact]
    public async Task A_401_flags_reauth_and_throws_not_connected()
    {
        var store = new FakeTokenStore(TimeProvider.System);
        var client = new FakeGmailClient(store, TimeProvider.System);
        client.FailNext(HttpStatusCode.Unauthorized, 1);

        await Should.ThrowAsync<GmailNotConnectedException>(() => client.GetMessagesMetadataAsync(["fake-msg-0001"], Ct));
        (await store.GetAsync(Ct))!.ReauthRequired.ShouldBeTrue();
        await Should.ThrowAsync<GmailNotConnectedException>(() => client.ListMessageIdsAsync(new MessageListQuery(null, null, null, 10), Ct));
    }

    [Fact]
    public async Task Other_errors_are_not_retried()
    {
        var client = Client();
        client.FailNext(HttpStatusCode.InternalServerError, 1);

        await Should.ThrowAsync<Google.GoogleApiException>(() => client.ListMessageIdsAsync(new MessageListQuery(null, null, null, 10), Ct));
        (await client.ListMessageIdsAsync(new MessageListQuery(null, null, null, 10), Ct)).Messages.Count.ShouldBe(10);
    }

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static FakeGmailClient Client(TimeProvider? time = null, int maxAttempts = 8)
    {
        time ??= TimeProvider.System;
        var retry = new GmailRetryPolicy(Options.Create(new GmailOptions { MaxRetryAttempts = maxAttempts }), time);
        return new FakeGmailClient(new FakeTokenStore(time), FakeGmailClient.Seed(Now), retry);
    }

    private static async Task<T> DriveAsync<T>(FakeTimeProvider time, Task<T> task)
    {
        for (var i = 0; i < 2000 && !task.IsCompleted; i++)
        {
            await Task.Delay(1, Ct);
            if (!task.IsCompleted)
            {
                time.Advance(TimeSpan.FromMilliseconds(250));
            }
        }

        return await task;
    }

    [Fact]
    public void FakeFilterStore_Create_never_reuses_an_id_after_a_restart()
    {
        var criteria = new GmailFilterCriteria(From: "bob@example.com");
        var action = new GmailFilterAction([], ["UNREAD"]);
        var before = new FakeFilterStore().Create(criteria, action);

        var afterRestart = new FakeFilterStore().Create(criteria, action);

        afterRestart.Id.ShouldNotBe(before.Id);
        FakeFilterStore.Seed.Select(f => f.Id).ShouldNotContain(afterRestart.Id);
    }
}
