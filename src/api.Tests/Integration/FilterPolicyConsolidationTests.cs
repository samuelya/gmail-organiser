using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Policies;
using GmailOrganiser.Rules;
using GmailOrganiser.Rules.Review;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// Filter review findings against the policy proposals (#374): two keep policies labelled with the fake's first label
/// each propose their own filter in the review (never one merged across policies), so a <c>from:deals subject:Weekly</c>
/// filter with that label overlaps the deals policy's and the seed's <c>from:news</c> filter, which also skips the inbox,
/// conflicts with the news policy's; a <c>from:shop</c> filter that trashes conflicts with a keep policy labelled with
/// the fake's third label.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FilterPolicyConsolidationTests(ApiFactory factory, PostgresFixture postgres)
    : PolicyFilterProposalTestBase(factory, postgres), IClassFixture<ApiFactory>
{
    private const string Deals = "deals@example.com";

    private FakeGmailClient Gmail => Host.Services.GetRequiredService<FakeGmailClient>();

    [Fact]
    public async Task An_overlapping_filter_is_merged_into_the_policy_filter()
    {
        var (deals, _, policies) = await SeedAsync();

        var finding = Find(await CreateReviewAsync(), FilterFindingKind.OverlapsPolicy);

        finding.FilterIds.ShouldBe([deals]);
        finding.PolicyId.ShouldBe(policies[Deals]);
        finding.Fix.Kind.ShouldBe(FilterFixKind.Merge);
        finding.Fix.Create.ShouldNotBeNull().Criteria.From.ShouldBe(Deals);
        (await ApplyAsync(finding.Id, HttpStatusCode.OK)).Status.ShouldBe(FilterFindingStatus.Applied);

        var gmail = await Gmail.ListFiltersAsync(Ct);
        gmail.ShouldNotContain(f => f.Id == deals);
        var created = gmail.Where(f => f.Criteria.From == Deals).ShouldHaveSingleItem();
        created.Action.AddLabelIds.ShouldBe(["Label_1"]);
        created.Action.RemoveLabelIds.ShouldBeEmpty();
        await using var db = Postgres.CreateDbContext();
        (await db.Filters.SingleAsync(r => r.Id == deals, Ct)).DeletedByApp.ShouldBeTrue();
        (await db.Filters.SingleAsync(r => r.Id == created.Id, Ct)).CreatedByApp.ShouldBeTrue();
    }

    [Fact]
    public async Task A_conflicting_filter_is_replaced_by_the_label_only_policy_filter()
    {
        var (_, shop, policies) = await SeedAsync();
        var review = await CreateReviewAsync();

        var news = review.Findings.Single(f => f.Kind == FilterFindingKind.PolicyConflict && f.FilterIds[0] == "fake-filter-1");
        news.Description.ShouldContain("skips the inbox");
        news.PolicyId.ShouldBe(policies["news@example.com"]);
        news.Fix.Create.ShouldNotBeNull().Criteria.From.ShouldBe("news@example.com");
        var trash = review.Findings.Single(f => f.Kind == FilterFindingKind.PolicyConflict && f.FilterIds[0] == shop);
        trash.PolicyId.ShouldBe(policies["shop@example.org"]);
        trash.Description.ShouldContain("never trashes");
        trash.Fix.Kind.ShouldBe(FilterFixKind.Relabel);

        (await ApplyAsync(trash.Id, HttpStatusCode.OK)).Status.ShouldBe(FilterFindingStatus.Applied);

        var gmail = await Gmail.ListFiltersAsync(Ct);
        gmail.ShouldNotContain(f => f.Id == shop);
        var created = gmail.Where(f => f.Criteria.From == "shop@example.org").ShouldHaveSingleItem();
        created.Action.AddLabelIds.ShouldBe(["Label_3"]);
        created.Action.RemoveLabelIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_dismissed_conflict_leaves_gmail_unchanged()
    {
        var (_, shop, _) = await SeedAsync();
        var finding = (await CreateReviewAsync()).Findings.Single(f => f.Kind == FilterFindingKind.PolicyConflict && f.FilterIds[0] == shop);
        var before = await Gmail.ListFiltersAsync(Ct);

        var response = await PostAsync($"/api/rules/filters/findings/{finding.Id}/dismiss");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<FilterFindingDto>(Ct)).ShouldNotBeNull().Status.ShouldBe(FilterFindingStatus.Dismissed);
        (await Gmail.ListFiltersAsync(Ct)).Select(f => f.Id).ShouldBe(before.Select(f => f.Id), ignoreOrder: true);
        (await PostAsync($"/api/rules/filters/findings/{finding.Id}/apply")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_re_run_finds_the_same_until_applied_and_then_not_the_applied_overlap()
    {
        await SeedAsync();
        var first = await CreateReviewAsync();
        var again = await CreateReviewAsync();
        Kinds(again).ShouldBe(Kinds(first));
        Kinds(again).Count(k => k is FilterFindingKind.OverlapsPolicy or FilterFindingKind.PolicyConflict).ShouldBe(3);

        var overlap = Find(again, FilterFindingKind.OverlapsPolicy);
        (await ApplyAsync(overlap.Id, HttpStatusCode.OK)).Status.ShouldBe(FilterFindingStatus.Applied);
        (await PostAsync($"/api/rules/filters/findings/{overlap.Id}/apply")).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // The created filter is the policy's own: no new overlap, and the two conflicts remain.
        var after = await CreateReviewAsync();
        Kinds(after).ShouldNotContain(FilterFindingKind.OverlapsPolicy);
        Kinds(after).Count(k => k == FilterFindingKind.PolicyConflict).ShouldBe(2);
        (await Gmail.ListFiltersAsync(Ct)).Count(f => f.Criteria.From == Deals).ShouldBe(1);
    }

    /// <summary>The policies, their senders and the two extra Gmail filters; returns the filter ids and the policy ids by key.</summary>
    private async Task<(string Deals, string Shop, Dictionary<string, Guid> Policies)> SeedAsync()
    {
        var policies = new[]
        {
            Policy(PolicyScope.Sender, Deals, PolicyAction.Keep, FakeLabelStore.SeedUserLabelNames[0]),
            Policy(PolicyScope.Sender, "news@example.com", PolicyAction.Keep, FakeLabelStore.SeedUserLabelNames[0]),
            Policy(PolicyScope.Sender, "shop@example.org", PolicyAction.Keep, FakeLabelStore.SeedUserLabelNames[2]),
        };
        await using (var db = Postgres.CreateDbContext())
        {
            await db.FilterReviews.ExecuteDeleteAsync(Ct);
            db.Senders.AddRange(policies.Select(p => Sender(p.ScopeKey, p.ScopeKey)));
            db.SenderPolicies.AddRange(policies);
            await db.SaveChangesAsync(Ct);
        }

        var deals = await Gmail.CreateFilterAsync(new GmailFilterCriteria(From: Deals, Subject: "Weekly"), new GmailFilterAction(["Label_1"], []), Ct);
        var shop = await Gmail.CreateFilterAsync(new GmailFilterCriteria(From: "shop@example.org"), new GmailFilterAction(["TRASH"], []), Ct);
        return (deals.Id, shop.Id, policies.ToDictionary(p => p.ScopeKey, p => p.Id));
    }

    private static List<FilterFindingKind> Kinds(FilterReviewDto review) => [.. review.Findings.Select(f => f.Kind).Order()];

    private static FilterFindingDto Find(FilterReviewDto review, FilterFindingKind kind) => review.Findings.Single(f => f.Kind == kind);

    private async Task<FilterReviewDto> CreateReviewAsync()
    {
        var response = await PostAsync("/api/rules/filters/reviews");
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<FilterReviewDto>(Ct)).ShouldNotBeNull();
    }

    private async Task<FilterFindingDto> ApplyAsync(Guid id, HttpStatusCode expected)
    {
        var response = await PostAsync($"/api/rules/filters/findings/{id}/apply");
        response.StatusCode.ShouldBe(expected);
        return (await response.Content.ReadFromJsonAsync<FilterFindingDto>(Ct)).ShouldNotBeNull();
    }

    private Task<HttpResponseMessage> PostAsync(string path)
    {
        var client = Host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client.PostAsync(path, null, Ct);
    }
}
