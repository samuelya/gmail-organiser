using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Review;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// The review <c>mailType</c> filter (#439) over a finished inbox run of the harness mailbox: shop's a00–a04 are
/// marketing and a05–a09 receipts, news's 6 newsletters, billing's 4 have no mail type.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReviewMailTypeFilterTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
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
        await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Shop)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.MailType, x => marketing.Contains(x.MessageId) ? MailType.Marketing : MailType.Receipt), Ct);
        await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.News)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.MailType, MailType.Newsletter), Ct);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Sender_list_takes_only_suggestions_of_the_given_mail_types()
    {
        var one = await GetAsync<PagedDto<ReviewSenderDto>>("/api/review/senders?mailType=marketing");
        (one.Total, one.Items.ShouldHaveSingleItem().Address, one.Items[0].Pending).ShouldBe((1, AnalysisRunHarness.Shop, 5));

        var two = await GetAsync<PagedDto<ReviewSenderDto>>("/api/review/senders?mailType=Newsletter,%20RECEIPT,receipt");
        two.Items.Select(s => (s.Address, s.Pending)).ShouldBe([(AnalysisRunHarness.News, 6), (AnalysisRunHarness.Shop, 5)]);

        (await GetAsync<PagedDto<ReviewSenderDto>>("/api/review/senders?mailType=")).Total.ShouldBe(3);
        (await GetAsync<PagedDto<ReviewSenderDto>>("/api/review/senders?mailType=social")).Total.ShouldBe(0);
        await BadRequestAsync("/api/review/senders?mailType=marketing,bogus");
    }

    [Fact]
    public async Task Sender_detail_lists_whole_groups_with_a_member_of_the_given_mail_types()
    {
        var path = $"/api/review/senders/{Uri.EscapeDataString(AnalysisRunHarness.Shop)}";

        var one = await GetAsync<ReviewSenderDetailDto>($"{path}?mailType=marketing");
        one.Sender.Pending.ShouldBe(5);
        var group = one.Groups.ShouldHaveSingleItem();
        group.Size.ShouldBe(10);
        group.Members.Count(m => m.MailType == "marketing").ShouldBe(5);
        group.Members.Count(m => m.MailType == "receipt").ShouldBe(5);

        (await GetAsync<ReviewSenderDetailDto>($"{path}?mailType=marketing,receipt")).Sender.Pending.ShouldBe(10);
        (await h.GetAsync($"{path}?mailType=newsletter")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await h.GetAsync($"/api/review/senders/{Uri.EscapeDataString(AnalysisRunHarness.Billing)}?mailType=receipt"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await BadRequestAsync($"{path}?mailType=bogus");
    }

    [Fact]
    public async Task Group_reject_on_a_filtered_card_rejects_exactly_the_members_shown()
    {
        var path = $"/api/review/senders/{Uri.EscapeDataString(AnalysisRunHarness.Shop)}";
        var group = (await GetAsync<ReviewSenderDetailDto>($"{path}?mailType=marketing")).Groups.ShouldHaveSingleItem();

        var response = await h.PostAsync("/api/review/groups/reject", new GroupDecisionRequest(AnalysisRunHarness.Shop, group.GroupKey));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<GroupDecisionResponse>(Ct)).ShouldNotBeNull().Changed.ShouldBe(group.Members.Count);

        await using var db = postgres.CreateDbContext();
        var rejected = await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Shop && s.Status == SuggestionStatus.Rejected)
            .Select(s => s.Id).ToListAsync(Ct);
        rejected.Order().ShouldBe(group.Members.Select(m => m.Id).Order());
    }

    private async Task<T> GetAsync<T>(string path)
        where T : class
    {
        var response = await h.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct)).ShouldNotBeNull();
    }

    private async Task BadRequestAsync(string path)
    {
        var response = await h.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull().Errors.Keys.ShouldBe(["mailType"]);
    }
}
