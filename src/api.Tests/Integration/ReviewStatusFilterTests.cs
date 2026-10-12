using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Dashboard;
using GmailOrganiser.Review;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// The review <c>status=not_rejected</c> filter (#536) over a finished inbox run of the harness mailbox: shop's a00 is
/// approved, a02 applied, a01/a03/a04 rejected and a05–a09 pending (a00–a04 marketing, a05–a09 receipts); news's 6
/// newsletters are pending; billing's 4 receipts are all rejected.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReviewStatusFilterTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        await using var db = postgres.CreateDbContext();
        string[] marketing = ["a00", "a01", "a02", "a03", "a04"];
        string[] rejected = ["a01", "a03", "a04"];
        await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Shop).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.MailType, x => marketing.Contains(x.MessageId) ? MailType.Marketing : MailType.Receipt)
            .SetProperty(x => x.Status, x => x.MessageId == "a00" ? SuggestionStatus.Approved
                : x.MessageId == "a02" ? SuggestionStatus.Applied
                : rejected.Contains(x.MessageId) ? SuggestionStatus.Rejected
                : SuggestionStatus.Pending), Ct);
        await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.News)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.MailType, MailType.Newsletter), Ct);
        await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Billing).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.MailType, MailType.Receipt).SetProperty(x => x.Status, SuggestionStatus.Rejected), Ct);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Sender_list_takes_every_status_but_rejected_ordered_by_their_sum()
    {
        var list = await GetAsync<PagedDto<ReviewSenderDto>>("/api/review/senders?status=NOT_REJECTED%20");
        list.Total.ShouldBe(2);
        list.Items.Select(s => (s.Address, s.Pending, s.Approved, s.Applied))
            .ShouldBe([(AnalysisRunHarness.Shop, 5, 1, 1), (AnalysisRunHarness.News, 6, 0, 0)]);

        (await GetAsync<PagedDto<ReviewSenderDto>>("/api/review/senders?status=pending")).Items.Select(s => s.Address)
            .ShouldBe([AnalysisRunHarness.News, AnalysisRunHarness.Shop]);
        (await GetAsync<PagedDto<ReviewSenderDto>>("/api/review/senders?status=rejected")).Items.Select(s => s.Address)
            .ShouldBe([AnalysisRunHarness.Billing, AnalysisRunHarness.Shop]);

        var paged = await GetAsync<PagedDto<ReviewSenderDto>>("/api/review/senders?status=not_rejected&page=2&pageSize=1");
        (paged.Total, paged.Items.ShouldHaveSingleItem().Address).ShouldBe((2, AnalysisRunHarness.News));
        (await GetAsync<PagedDto<ReviewSenderDto>>("/api/review/senders?status=not_rejected&mailType=receipt"))
            .Items.ShouldHaveSingleItem().Address.ShouldBe(AnalysisRunHarness.Shop);
    }

    [Fact]
    public async Task Sender_list_per_mail_type_covers_what_the_dashboard_breakdown_counts()
    {
        var breakdown = await GetAsync<MailboxBreakdownDto>("/api/dashboard/breakdown");
        breakdown.MailTypes.Sum(t => t.Count).ShouldBe(13);

        foreach (var type in breakdown.MailTypes)
        {
            var list = await GetAsync<PagedDto<ReviewSenderDto>>($"/api/review/senders?status=not_rejected&mailType={type.MailType}");
            list.Items.Sum(s => s.Pending + s.Approved + s.Applied).ShouldBe(type.Count, type.MailType);
        }
    }

    [Fact]
    public async Task Sender_detail_lists_one_card_per_status_with_counts_and_alternatives_of_that_status()
    {
        foreach (var id in new[] { "a00", "a02", "a05", "a06" })
        {
            await AddAlternativeAsync(id);
        }

        var detail = await GetAsync<ReviewSenderDetailDto>(
            $"/api/review/senders/{Uri.EscapeDataString(AnalysisRunHarness.Shop)}?status=not_rejected");
        detail.Groups.Select(g => (g.GroupKey, g.Status)).ShouldBeUnique();
        detail.Groups.GroupBy(g => g.Status).ToDictionary(g => g.Key, g => g.Sum(x => x.Size))
            .ShouldBe(new Dictionary<string, int> { ["pending"] = 5, ["approved"] = 1, ["applied"] = 1 }, ignoreOrder: true);
        foreach (var g in detail.Groups)
        {
            g.Members.ShouldAllBe(m => m.Status == g.Status);
            g.Size.ShouldBe(g.Members.Count);
            (g.Alternative?.Count ?? 0).ShouldBe(g.Members.Count(m => m.Alternative != null), $"{g.GroupKey} {g.Status}");
        }

        detail.Groups.Sum(g => g.Alternative?.Count ?? 0).ShouldBe(4);
        (await GetAsync<ReviewSenderDetailDto>($"/api/review/senders/{Uri.EscapeDataString(AnalysisRunHarness.Shop)}"))
            .Groups.ShouldAllBe(g => g.Status == "pending");
    }

    [Theory]
    [InlineData("not_rejected")]
    [InlineData("pending")]
    public async Task Sender_detail_with_nothing_in_the_filter_is_ok_with_no_groups(string status)
    {
        var billing = $"/api/review/senders/{Uri.EscapeDataString(AnalysisRunHarness.Billing)}?status={status}";
        (await GetAsync<ReviewSenderDetailDto>(billing)).Groups.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/api/review/senders?status=notrejected")]
    [InlineData("/api/review/senders/shop%40example.com?status=all")]
    public async Task Unknown_status_lists_not_rejected_in_the_error(string path)
    {
        var response = await h.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull().Errors;
        errors["status"].ShouldBe([ReviewQuery.StatusFilterError]);
    }

    private async Task AddAlternativeAsync(string messageId)
    {
        await using var db = postgres.CreateDbContext();
        var id = await db.Suggestions.Where(s => s.MessageId == messageId).Select(s => s.Id).SingleAsync(Ct);
        db.SuggestionAlternatives.Add(new SuggestionAlternativeRow
        {
            Id = Guid.CreateVersion7(),
            SuggestionId = id,
            MessageId = messageId,
            Source = SuggestionSource.Llm,
            TopicLabel = "Synthetic",
            Confidence = 0.8,
            Reason = "Synthetic reason",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<T> GetAsync<T>(string path)
        where T : class
    {
        var response = await h.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct)).ShouldNotBeNull();
    }
}
