using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Review;
using GmailOrganiser.Rules;
using GmailOrganiser.Rules.Review;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// A filter with several user labels (#557): Gmail allows one per filter, so the app creates one Gmail filter per label
/// with the same criteria and system actions, and lists, deletes and restores them as one.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FilterGroupTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Shop = "shop@example.com";
    private const string Topic = "Synthetic Group/Topic";
    private const string ExtraA = "Synthetic Group/Extra A";
    private const string ExtraB = "Synthetic Group/Extra B";

    private WebApplicationFactory<Program> host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeGmailClient Gmail => host.Services.GetRequiredService<FakeGmailClient>();

    private CountingGmailClient Counting => host.Services.GetRequiredService<CountingGmailClient>();

    public async ValueTask InitializeAsync()
    {
        await postgres.ResetFetchStateAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.FilterReviews.ExecuteDeleteAsync(Ct);
            await db.ActionLog.ExecuteDeleteAsync(Ct);
            await db.ActionBatches.ExecuteDeleteAsync(Ct);
            await db.Filters.ExecuteDeleteAsync(Ct);
            await db.FetchState.ExecuteUpdateAsync(s => s.SetProperty(r => r.FiltersSyncedAt, (DateTimeOffset?)null), Ct);
            await db.Settings.ExecuteDeleteAsync(Ct);
        }

        host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true").ConfigureTestServices(services =>
        {
            var retry = new GmailRetryPolicy(Options.Create(new GmailOptions { MaxRetryAttempts = 1 }), TimeProvider.System);
            services.AddSingleton(sp => new FakeGmailClient(sp.GetRequiredService<FakeTokenStore>(), [], retry));
            services.AddSingleton(sp => new CountingGmailClient(sp.GetRequiredService<FakeGmailClient>()));
            services.AddScoped<IGmailClient>(sp => sp.GetRequiredService<CountingGmailClient>());
        }));
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync(Ct);
    }

    [Fact]
    public async Task A_topic_and_two_extra_labels_make_three_gmail_filters_shown_deleted_and_restored_as_one()
    {
        var response = await CreateAsync(Topic, ExtraA, ExtraB);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var filter = (await response.Content.ReadFromJsonAsync<FilterDto>(Ct)).ShouldNotBeNull();
        filter.FilterIds.Count.ShouldBe(3);
        filter.Action.AddLabels.Select(l => l.Name).ShouldBe([Topic, ExtraA, ExtraB]);
        (filter.Action.SkipInbox, filter.Action.MarkRead).ShouldBe((true, true));
        var parts = await ShopFiltersAsync();
        parts.Select(f => f.Id).ShouldBe(filter.FilterIds, ignoreOrder: true);
        parts.ShouldAllBe(f => f.Action.AddLabelIds.Count == 1 && f.Action.RemoveLabelIds.SequenceEqual(new[] { "INBOX", "UNREAD" }));

        var list = await ListAsync();
        list.ActiveCount.ShouldBe(FakeFilterStore.Seed.Count + 3);
        list.Filters.Single(f => f.Criteria.From == Shop).FilterIds.ShouldBe(filter.FilterIds, ignoreOrder: true);
        await using (var db = postgres.CreateDbContext())
        {
            (await db.ActionBatches.SingleAsync(Ct)).Kind.ShouldBe(ActionKind.FilterLabels);
            (await db.Filters.Where(r => filter.FilterIds.Contains(r.Id)).Select(r => r.GroupId).Distinct().SingleAsync(Ct)).ShouldNotBeNull();
        }

        (await Client().DeleteAsync($"/api/rules/filters/{filter.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ShopFiltersAsync()).ShouldBeEmpty();
        (await ListAsync()).Filters.ShouldNotContain(f => f.Criteria.From == Shop);
        (await ListAsync(includeDeleted: true)).Filters.Single(f => f.Criteria.From == Shop).FilterIds.Count.ShouldBe(3);

        var restored = await Client().PostAsync($"/api/rules/filters/{filter.FilterIds[1]}/restore", null, Ct);

        restored.StatusCode.ShouldBe(HttpStatusCode.Created);
        var back = (await restored.Content.ReadFromJsonAsync<FilterDto>(Ct)).ShouldNotBeNull();
        back.FilterIds.Count.ShouldBe(3);
        back.Action.AddLabels.Select(l => l.Name).ShouldBe([Topic, ExtraA, ExtraB], ignoreOrder: true);
        (await ShopFiltersAsync()).Select(f => f.Id).ShouldBe(back.FilterIds, ignoreOrder: true);
        (await Client().PostAsync($"/api/rules/filters/{filter.Id}/restore", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_part_gmail_refuses_deletes_the_parts_created_before_it_and_keeps_the_label_record()
    {
        (await CreateAsync(ExtraB)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var single = (await ShopFiltersAsync()).ShouldHaveSingleItem();

        var response = await CreateAsync(ExtraA, ExtraB);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Filter already exists");
        (await ShopFiltersAsync()).ShouldHaveSingleItem().Id.ShouldBe(single.Id);
        await using var db = postgres.CreateDbContext();
        (await db.Filters.CountAsync(r => r.DeletedAt == null && r.CreatedByApp, Ct)).ShouldBe(1);
        var extraA = (await Gmail.ListLabelsAsync(Ct)).Single(l => l.Name == ExtraA);
        (await db.ActionBatches.ToListAsync(Ct)).ShouldContain(b => b.CreatedLabelIds.Contains(extraA.Id));
    }

    [Fact]
    public async Task A_rollback_delete_lost_to_a_transport_error_keeps_the_row_and_surfaces_the_first_error()
    {
        (await CreateAsync(ExtraB)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var single = (await ShopFiltersAsync()).ShouldHaveSingleItem();
        Counting.DeleteFilterFailure = new HttpRequestException("Synthetic connection reset");

        var response = await CreateAsync(ExtraA, ExtraB);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Filter already exists");
        var left = (await ShopFiltersAsync()).Where(f => f.Id != single.Id).ShouldHaveSingleItem();
        await using var db = postgres.CreateDbContext();
        (await db.Filters.SingleAsync(r => r.Id == left.Id, Ct)).DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task An_overlap_fix_puts_the_kept_filter_and_the_new_part_in_one_group()
    {
        var kept = await CreatedAsync(Topic);
        var extraA = (await CreatedAsync(ExtraA)).Action.AddLabels.Single().Id;
        var other = (await ShopFiltersAsync()).Single(f => f.Id != kept.Id).Id;
        (await WithFilterServiceAsync(s => s.DeleteAsync(other, Ct))).Outcome.ShouldBe(FilterOutcome.Ok);
        var inboxOnly = await WithFilterServiceAsync(s => s.CreateFromIdsAsync(new(From: Shop), new([extraA], ["INBOX"]), 0, Ct));
        var review = await ReviewAsync();
        var overlap = review.Findings.Single(f => f.Kind == FilterFindingKind.Overlap && f.FilterIds.Contains(kept.Id));

        (await Client().PostAsync($"/api/rules/filters/findings/{overlap.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var shop = await ShopFiltersAsync();
        shop.Count.ShouldBe(2);
        shop.ShouldContain(f => f.Id == kept.Id);
        shop.ShouldNotContain(f => f.Id == inboxOnly.Filter!.Id);
        var listed = (await ListAsync()).Filters.Single(f => f.Criteria.From == Shop);
        listed.FilterIds.ShouldBe(shop.Select(f => f.Id), ignoreOrder: true);
    }

    [Fact]
    public async Task A_delete_fix_for_a_whole_group_deletes_it_as_one_and_restores_it_as_one()
    {
        var filter = await CreatedAsync(Topic, ExtraA);
        var findingId = await AddFindingAsync(new FilterFix(FilterFixKind.Delete, [.. filter.FilterIds]), [.. filter.FilterIds]);

        (await Client().PostAsync($"/api/rules/filters/findings/{findingId}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ShopFiltersAsync()).ShouldBeEmpty();
        var deleted = (await ListAsync(includeDeleted: true)).Filters.Where(f => f.Criteria.From == Shop).ShouldHaveSingleItem();
        deleted.FilterIds.ShouldBe(filter.FilterIds, ignoreOrder: true);
        var restored = await Client().PostAsync($"/api/rules/filters/{filter.FilterIds[0]}/restore", null, Ct);
        restored.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await restored.Content.ReadFromJsonAsync<FilterDto>(Ct)).ShouldNotBeNull().FilterIds.Count.ShouldBe(2);
        (await ListAsync()).Filters.Single(f => f.Criteria.From == Shop).FilterIds.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_review_reports_no_duplicate_or_overlap_for_a_split_group()
    {
        var filter = (await (await CreateAsync(Topic, ExtraA)).Content.ReadFromJsonAsync<FilterDto>(Ct)).ShouldNotBeNull();

        var response = await Client().PostAsync("/api/rules/filters/reviews", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var review = (await response.Content.ReadFromJsonAsync<FilterReviewDto>(Ct)).ShouldNotBeNull();
        review.Findings.ShouldNotContain(f =>
            (f.Kind == FilterFindingKind.Duplicate || f.Kind == FilterFindingKind.Overlap) && f.FilterIds.Intersect(filter.FilterIds).Any());
    }

    [Fact]
    public async Task Retargeting_a_member_onto_a_label_its_group_already_has_only_deletes_it()
    {
        var filter = await CreatedAsync(Topic, ExtraA);
        var (topic, extraA) = (filter.Action.AddLabels[0].Id, filter.Action.AddLabels[1].Id);
        var member = (await ShopFiltersAsync()).Single(f => f.Action.AddLabelIds.Contains(extraA));

        var result = await WithFilterServiceAsync(s => s.RetargetAsync(member.Id, extraA, topic, Ct));

        result.Outcome.ShouldBe(FilterOutcome.Ok);
        (await ShopFiltersAsync()).ShouldHaveSingleItem().Action.AddLabelIds.ShouldBe([topic]);
    }

    [Fact]
    public async Task Restoring_a_member_deleted_alone_rejoins_its_group()
    {
        var filter = await CreatedAsync(Topic, ExtraA);
        var member = filter.FilterIds[1];
        (await WithFilterServiceAsync(s => s.DeleteAsync(member, Ct))).Outcome.ShouldBe(FilterOutcome.Ok);

        (await Client().PostAsync($"/api/rules/filters/{member}/restore", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await ListAsync()).Filters.Single(f => f.Criteria.From == Shop).FilterIds.Count.ShouldBe(2);
        await using var db = postgres.CreateDbContext();
        var group = (await db.Filters.SingleAsync(r => r.Id == filter.FilterIds[0], Ct)).GroupId;
        (await db.Filters.SingleAsync(r => r.RestoredFrom == member, Ct)).GroupId.ShouldBe(group);
    }

    [Fact]
    public async Task Applying_a_fix_whose_split_filters_already_exist_reuses_them_and_only_deletes()
    {
        var filter = await CreatedAsync(Topic, ExtraA);
        var old = (await CreatedAsync(ExtraB)).Id;
        var fix = new FilterFix(FilterFixKind.MergeActions, [old], new(
            new GmailFilterCriteria(From: Shop),
            new GmailFilterAction([.. filter.Action.AddLabels.Select(l => l.Id)], ["INBOX", "UNREAD"])));
        var findingId = await AddFindingAsync(fix, [.. filter.FilterIds, old]);

        var response = await Client().PostAsync($"/api/rules/filters/findings/{findingId}/apply", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ShopFiltersAsync()).Select(f => f.Id).ShouldBe(filter.FilterIds, ignoreOrder: true);
        await using var db = postgres.CreateDbContext();
        (await db.FilterFindings.SingleAsync(f => f.Id == findingId, Ct)).Status.ShouldBe(FilterFindingStatus.Applied);
    }

    [Fact]
    public async Task A_fix_whose_parts_exist_as_ungrouped_filters_makes_them_one_group()
    {
        var topic = await CreatedAsync(Topic);
        var extraA = await CreatedAsync(ExtraA);
        var old = (await CreatedAsync(ExtraB)).Id;
        var fix = new FilterFix(FilterFixKind.MergeActions, [old], new(
            new GmailFilterCriteria(From: Shop),
            new GmailFilterAction([topic.Action.AddLabels.Single().Id, extraA.Action.AddLabels.Single().Id], ["INBOX", "UNREAD"])));
        var findingId = await AddFindingAsync(fix, [topic.Id, extraA.Id, old]);

        (await Client().PostAsync($"/api/rules/filters/findings/{findingId}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ShopFiltersAsync()).Select(f => f.Id).ShouldBe([topic.Id, extraA.Id], ignoreOrder: true);
        await using var db = postgres.CreateDbContext();
        var groups = await db.Filters.Where(r => r.Id == topic.Id || r.Id == extraA.Id).Select(r => r.GroupId).ToListAsync(Ct);
        groups.ShouldAllBe(g => g != null && g == groups[0]);
        (await ListAsync()).Filters.Single(f => f.Criteria.From == Shop).FilterIds.ShouldBe([topic.Id, extraA.Id], ignoreOrder: true);
    }

    private async Task<FilterReviewDto> ReviewAsync()
    {
        var response = await Client().PostAsync("/api/rules/filters/reviews", null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<FilterReviewDto>(Ct)).ShouldNotBeNull();
    }

    private async Task<FilterDto> CreatedAsync(params string[] labels) =>
        (await (await CreateAsync(labels)).Content.ReadFromJsonAsync<FilterDto>(Ct)).ShouldNotBeNull();

    private async Task<FilterResult> WithFilterServiceAsync(Func<FilterService, Task<FilterResult>> call)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<FilterService>());
    }

    private async Task<Guid> AddFindingAsync(FilterFix fix, List<string> filterIds)
    {
        await using var db = postgres.CreateDbContext();
        var review = new FilterReviewRow { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, FilterCount = filterIds.Count, FindingCount = 1 };
        var finding = new FilterFindingRow
        {
            Id = Guid.NewGuid(),
            ReviewId = review.Id,
            Kind = FilterFindingKind.Overlap,
            FilterIds = filterIds,
            Description = "Synthetic finding",
            Status = FilterFindingStatus.Open,
        };
        finding.WriteFix(fix);
        db.FilterReviews.Add(review);
        db.FilterFindings.Add(finding);
        await db.SaveChangesAsync(Ct);
        return finding.Id;
    }

    private Task<HttpResponseMessage> CreateAsync(params string[] labels) =>
        Client().PostAsync("/api/rules/filters", JsonContent.Create(new CreateFilterRequest(
            new FilterCriteriaDto(Shop, null, null, null, null, null, null, null, null),
            new FilterActionRequest(labels, SkipInbox: true, MarkRead: true))), Ct);

    private async Task<List<GmailFilter>> ShopFiltersAsync() =>
        [.. (await Gmail.ListFiltersAsync(Ct)).Where(f => f.Criteria.From == Shop)];

    private async Task<FilterListDto> ListAsync(bool includeDeleted = false) =>
        (await host.CreateClient().GetFromJsonAsync<FilterListDto>($"/api/rules/filters?includeDeleted={includeDeleted}", Ct)).ShouldNotBeNull();

    private HttpClient Client()
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    }
}
