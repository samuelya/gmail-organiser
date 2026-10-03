using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.CleanUp.Unsubscribe;
using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Senders;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class UnsubscribeEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Domain = "unsub.example.com";
    private const string OneClickSender = "news@unsub.example.com";
    private const string LinkSender = "shop@unsub.example.com";
    private const string HttpSender = "plain@unsub.example.com";
    private const string MailtoSender = "forum@unsub.example.com";
    private const string NoHeaderSender = "friend@unsub.example.com";
    private const string OneClickPost = "List-Unsubscribe=One-Click";
    private static readonly DateTimeOffset Newest = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeUnsubscribeSender unsubscriber = new();
    private WebApplicationFactory<Program> host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Messages stored in the database; <c>gone</c> ones are no longer in Gmail. The one-click sender's newest
    /// message is gone from Gmail and its next one carries the post header; its oldest message is soft-deleted.
    /// </summary>
    private static readonly (string Id, string From, string? Header, string? Post, int HoursAgo, bool Gone, bool Deleted)[] Mail =
    [
        ("u-n0", OneClickSender, "<https://example.com/u/n0>", OneClickPost, 0, true, false),
        ("u-n1", OneClickSender, "<mailto:u@example.com>, <https://example.com/u/n1>", OneClickPost, 1, false, false),
        ("u-n2", OneClickSender, "<https://example.com/u/n2>", null, 2, false, false),
        ("u-n3", OneClickSender, "<https://example.com/u/deleted>", OneClickPost, -5, false, true),
        ("u-s0", LinkSender, "<https://example.com/u/s0>, <http://example.com/u/s0>", null, 0, false, false),
        ("u-s1", LinkSender, null, null, -1, false, false),
        ("u-p0", HttpSender, "<http://example.com/u/p0>", OneClickPost, 0, false, false),
        ("u-f0", MailtoSender, "<mailto:unsubscribe@example.com?subject=unsubscribe%20me>", OneClickPost, 0, false, false),
        ("u-x0", NoHeaderSender, null, null, 0, false, false),
    ];

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => m.FromAddress.EndsWith("@" + Domain)).ExecuteDeleteAsync(Ct);
            await db.Senders.Where(s => s.Domain == Domain).ExecuteDeleteAsync(Ct);
            db.Messages.AddRange(Mail.Select(m => new MessageRow
            {
                Id = m.Id,
                ThreadId = $"t-{m.Id}",
                FromAddress = m.From,
                InternalDate = Newest.AddHours(-m.HoursAgo),
                ListUnsubscribe = m.Header,
                DeletedInGmail = m.Deleted,
                FetchedAt = Newest,
                UpdatedAt = Newest,
            }));
            db.Senders.AddRange(Mail.Select(m => m.From).Distinct().Select(a => new SenderRow
            {
                Address = a,
                Domain = Domain,
                TotalCount = Mail.Count(m => m.From == a),
                UpdatedAt = Newest,
            }));
            await db.SaveChangesAsync(Ct);
        }

        FakeMessage[] gmail = [.. Mail.Where(m => !m.Gone).Select(m => new FakeMessage(
            m.Id, $"t-{m.Id}", m.From, "Synthetic", Newest.AddHours(-m.HoursAgo), ["INBOX"],
            ListUnsubscribe: m.Header, ListUnsubscribePost: m.Post))];
        host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true").ConfigureTestServices(services =>
        {
            services.AddSingleton(sp => new FakeGmailClient(sp.GetRequiredService<FakeTokenStore>(), gmail));
            services.AddScoped<IGmailClient>(sp => sp.GetRequiredService<FakeGmailClient>());
            services.AddSingleton<IUnsubscribeSender>(unsubscriber);
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        }));
    }

    public async ValueTask DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task One_click_skips_a_message_gmail_no_longer_knows_and_a_deleted_one()
    {
        var info = await InfoAsync(OneClickSender);

        info.ShouldBe(new UnsubscribeInfoDto(UnsubscribeMethod.OneClick, "https://example.com/u/n1", "u-n1", null, null));
    }

    [Fact]
    public async Task Link_mailto_and_none_are_resolved_from_the_newest_message_with_the_header()
    {
        (await InfoAsync(LinkSender)).ShouldBe(new UnsubscribeInfoDto(UnsubscribeMethod.Link, "https://example.com/u/s0", "u-s0", null, null));
        (await InfoAsync(HttpSender)).ShouldBe(new UnsubscribeInfoDto(UnsubscribeMethod.Link, "http://example.com/u/p0", "u-p0", null, null));
        (await InfoAsync(MailtoSender)).ShouldBe(new UnsubscribeInfoDto(
            UnsubscribeMethod.Mailto, "mailto:unsubscribe@example.com?subject=unsubscribe%20me", "u-f0", null, null));
        (await InfoAsync(NoHeaderSender)).ShouldBe(new UnsubscribeInfoDto(null, null, null, null, null));
        (await Client().GetAsync("/api/clean-up/senders/nobody@unsub.example.com/unsubscribe", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Json_uses_snake_case_methods()
    {
        var json = await Client().GetStringAsync($"/api/clean-up/senders/{Uri.EscapeDataString(OneClickSender.ToUpperInvariant())}/unsubscribe", Ct);

        json.ShouldContain("\"method\":\"one_click\"");
    }

    [Fact]
    public async Task One_click_done_marks_the_sender_and_shows_on_the_senders_list()
    {
        var response = await PostAsync($"/api/clean-up/senders/{OneClickSender}/unsubscribe", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<UnsubscribeResultDto>(Ct)).ShouldBe(new UnsubscribeResultDto("done", 200));
        unsubscriber.Sent.ShouldBe([new Uri("https://example.com/u/n1")]);
        (await InfoAsync(OneClickSender)).ShouldBe(new UnsubscribeInfoDto(
            UnsubscribeMethod.OneClick, "https://example.com/u/n1", "u-n1", Now, UnsubscribeMethod.OneClick));
        var senders = await Client().GetFromJsonAsync<PagedDto<SenderDto>>($"/api/senders?search={Domain}", Ct);
        senders!.Items.Single(s => s.Address == OneClickSender).UnsubscribedAt.ShouldBe(Now);
        senders.Items.Where(s => s.Address != OneClickSender).ShouldAllBe(s => s.UnsubscribedAt == null);

        // An unsubscribed sender can be sent again.
        (await PostAsync($"/api/clean-up/senders/{OneClickSender}/unsubscribe", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        unsubscriber.Sent.Count.ShouldBe(2);
    }

    [Fact]
    public async Task One_click_failure_leaves_the_sender_unmarked()
    {
        unsubscriber.Respond = (_, _) => Task.FromResult(new UnsubscribeSendResult(false, 302));

        var response = await PostAsync($"/api/clean-up/senders/{OneClickSender}/unsubscribe", null);

        (await response.Content.ReadFromJsonAsync<UnsubscribeResultDto>(Ct)).ShouldBe(new UnsubscribeResultDto("failed", 302));
        (await StoredAsync(OneClickSender)).ShouldBe((null, null));
    }

    [Fact]
    public async Task One_click_done_is_recorded_even_when_the_request_is_cancelled_after_sending()
    {
        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        unsubscriber.Respond = async (_, _) =>
        {
            await aborted.CancelAsync();
            return new UnsubscribeSendResult(true, 200);
        };

        await using var scope = host.Services.CreateAsyncScope();
        var (outcome, result) = await scope.ServiceProvider.GetRequiredService<UnsubscribeService>()
            .SendAsync(OneClickSender, aborted.Token);

        outcome.ShouldBe(UnsubscribeOutcome.Ok);
        result.ShouldBe(new UnsubscribeResultDto("done", 200));
        (await StoredAsync(OneClickSender)).ShouldBe((Now, UnsubscribeMethod.OneClick));
    }

    [Theory]
    [InlineData(LinkSender)]
    [InlineData(MailtoSender)]
    [InlineData(NoHeaderSender)]
    public async Task Post_without_one_click_is_409_and_sends_nothing(string sender)
    {
        var response = await PostAsync($"/api/clean-up/senders/{sender}/unsubscribe", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("No one-click unsubscribe");
        unsubscriber.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_second_post_while_one_runs_is_409()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        unsubscriber.Respond = async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return new UnsubscribeSendResult(true, 204);
        };

        var first = PostAsync($"/api/clean-up/senders/{OneClickSender}/unsubscribe", null);
        await entered.Task.WaitAsync(Ct);
        var second = await PostAsync($"/api/clean-up/senders/{OneClickSender}/unsubscribe", null);
        release.SetResult();

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync(Ct)).ShouldContain("in progress");
        (await (await first).Content.ReadFromJsonAsync<UnsubscribeResultDto>(Ct)).ShouldBe(new UnsubscribeResultDto("done", 204));
    }

    [Theory]
    [InlineData(LinkSender, "link", UnsubscribeMethod.Link)]
    [InlineData(MailtoSender, "mailto", UnsubscribeMethod.Mailto)]
    public async Task Mark_records_a_link_or_mailto_opened_by_hand(string sender, string method, UnsubscribeMethod stored)
    {
        var response = await PostAsync($"/api/clean-up/senders/{sender}/unsubscribe/mark", new { method });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StoredAsync(sender)).ShouldBe((Now, stored));
        unsubscriber.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Mark_rejects_one_click_unknown_methods_and_unknown_senders()
    {
        (await PostAsync($"/api/clean-up/senders/{LinkSender}/unsubscribe/mark", new { method = "one_click" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync($"/api/clean-up/senders/{LinkSender}/unsubscribe/mark", new { method = "carrier_pigeon" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync("/api/clean-up/senders/nobody@unsub.example.com/unsubscribe/mark", new { method = "link" }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await StoredAsync(LinkSender)).ShouldBe((null, null));
    }

    [Fact]
    public async Task Gmail_not_connected_is_503_for_a_https_target()
    {
        await host.Services.GetRequiredService<FakeTokenStore>().DeleteAsync(Ct);

        (await Client().GetAsync($"/api/clean-up/senders/{OneClickSender}/unsubscribe", Ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await PostAsync($"/api/clean-up/senders/{OneClickSender}/unsubscribe", null)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        unsubscriber.Sent.ShouldBeEmpty();
    }

    private HttpClient Client() => host.CreateClient();

    private async Task<UnsubscribeInfoDto> InfoAsync(string sender) =>
        (await Client().GetFromJsonAsync<UnsubscribeInfoDto>($"/api/clean-up/senders/{sender}/unsubscribe", Ct)).ShouldNotBeNull();

    private Task<HttpResponseMessage> PostAsync(string path, object? body)
    {
        var client = Client();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return body is null ? client.PostAsync(path, null, Ct) : client.PostAsJsonAsync(path, body, Ct);
    }

    private async Task<(DateTimeOffset? At, UnsubscribeMethod? Via)> StoredAsync(string sender)
    {
        await using var db = postgres.CreateDbContext();
        var row = await db.Senders.AsNoTracking().SingleAsync(s => s.Address == sender, Ct);
        return (row.UnsubscribedAt, row.UnsubscribeMethod);
    }
}
