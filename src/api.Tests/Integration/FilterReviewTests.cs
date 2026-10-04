using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
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
/// Filter reviews over the fake's seed filters (<c>from:news</c>, a receipt subject that matches nothing, <c>to:lists</c>
/// adding a missing label) plus <c>from:shop</c> with news' action (mergeable) and an offer subject that adds a missing
/// label besides <c>Label_1</c> (drop_label).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FilterReviewTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Shop = "shop@example.com";
    private const string News = "news@example.com";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private WebApplicationFactory<Program> host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeGmailClient Gmail => host.Services.GetRequiredService<FakeGmailClient>();

    public async ValueTask InitializeAsync()
    {
        await postgres.ResetFetchStateAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.FilterReviews.ExecuteDeleteAsync(Ct);
            await db.Filters.ExecuteDeleteAsync(Ct);
            await db.FetchState.ExecuteUpdateAsync(s => s.SetProperty(r => r.FiltersSyncedAt, (DateTimeOffset?)null), Ct);
            await db.Suggestions.ExecuteDeleteAsync(Ct);
            await db.Messages.ExecuteDeleteAsync(Ct);
            await db.Settings.ExecuteDeleteAsync(Ct);
            db.Messages.AddRange(Mailbox().Select(m => new MessageRow
            {
                Id = m.Id,
                ThreadId = m.ThreadId,
                FromAddress = m.From,
                Subject = m.Subject,
                ToHeader = m.To,
                InternalDate = m.Date,
                LabelIds = [.. m.LabelIds],
                FetchedAt = Now,
                UpdatedAt = Now,
            }));
            await db.SaveChangesAsync(Ct);
        }

        host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true").ConfigureTestServices(services =>
        {
            var retry = new GmailRetryPolicy(Options.Create(new GmailOptions { MaxRetryAttempts = 1 }), TimeProvider.System);
            services.AddSingleton(sp => new FakeGmailClient(sp.GetRequiredService<FakeTokenStore>(), Mailbox(), retry));
            services.AddScoped<IGmailClient>(sp => sp.GetRequiredService<FakeGmailClient>());
        }));
        await Gmail.CreateFilterAsync(new GmailFilterCriteria(From: Shop), new GmailFilterAction(["Label_1"], ["INBOX"]), Ct);
        await Gmail.CreateFilterAsync(
            new GmailFilterCriteria(Subject: "Weekly offer"), new GmailFilterAction(["Label_1", FakeFilterStore.MissingLabelId], []), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync(Ct);
    }

    [Fact]
    public async Task A_review_stores_one_finding_per_check_and_is_served_as_the_latest()
    {
        var review = await CoveredReviewAsync();

        review.FilterCount.ShouldBe(5);
        // Filters first seen together are checked in id order, and the fake's new filter ids are random.
        review.Findings.Select(f => (f.Kind, f.Fix.Kind)).ShouldBe(
        [
            (FilterFindingKind.DeletedLabel, FilterFixKind.Delete),
            (FilterFindingKind.DeletedLabel, FilterFixKind.DropLabel),
            (FilterFindingKind.NoRecentMatches, FilterFixKind.Delete),
            (FilterFindingKind.Mergeable, FilterFixKind.Merge),
        ], ignoreOrder: true);
        review.Findings.ShouldAllBe(f => f.Status == FilterFindingStatus.Open && f.Filters.Count == f.FilterIds.Count);
        Find(review, FilterFindingKind.NoRecentMatches).FilterIds.ShouldBe(["fake-filter-2"]);
        Find(review, FilterFindingKind.Mergeable).Description.ShouldContain("Example");

        var latest = await Client().GetFromJsonAsync<FilterReviewDto>("/api/rules/filters/reviews/latest", Ct);
        latest.ShouldNotBeNull().Id.ShouldBe(review.Id);
        latest.Findings.Select(f => f.Id).ShouldBe(review.Findings.Select(f => f.Id));
        (await Client().GetFromJsonAsync<FilterReviewDto>($"/api/rules/filters/reviews/{review.Id}", Ct)).ShouldNotBeNull();
        (await Client().GetAsync($"/api/rules/filters/reviews/{Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Latest_is_404_before_the_first_review() =>
        (await Client().GetAsync("/api/rules/filters/reviews/latest", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

    [Fact]
    public async Task Applying_a_merge_creates_one_filter_and_deletes_the_originals()
    {
        var finding = Find(await CreateReviewAsync(), FilterFindingKind.Mergeable);

        var applied = await ApplyAsync(finding.Id, HttpStatusCode.OK);

        applied.Status.ShouldBe(FilterFindingStatus.Applied);
        applied.AppliedAt.ShouldNotBeNull();
        var gmail = await Gmail.ListFiltersAsync(Ct);
        gmail.ShouldNotContain(f => finding.FilterIds.Contains(f.Id));
        var merged = gmail.Where(f => IsMerged(f)).ShouldHaveSingleItem();
        merged.Action.AddLabelIds.ShouldBe(["Label_1"]);
        await using var db = postgres.CreateDbContext();
        (await db.Filters.Where(r => finding.FilterIds.Contains(r.Id)).ToListAsync(Ct)).ShouldAllBe(r => r.DeletedByApp);
        (await db.Filters.SingleAsync(r => r.Id == merged.Id, Ct)).CreatedByApp.ShouldBeTrue();
    }

    [Fact]
    public async Task Applying_a_drop_label_recreates_the_filter_without_the_missing_label()
    {
        var finding = Find(await CreateReviewAsync(), FilterFindingKind.DeletedLabel, FilterFixKind.DropLabel);

        (await ApplyAsync(finding.Id, HttpStatusCode.OK)).Status.ShouldBe(FilterFindingStatus.Applied);

        var gmail = await Gmail.ListFiltersAsync(Ct);
        gmail.ShouldNotContain(f => f.Id == finding.FilterIds[0]);
        gmail.Where(f => f.Criteria.Subject == "Weekly offer").ShouldHaveSingleItem().Action.AddLabelIds.ShouldBe(["Label_1"]);
    }

    [Fact]
    public async Task A_failure_mid_way_leaves_the_finding_open_and_a_re_apply_finishes_without_creating_twice()
    {
        var finding = Find(await CreateReviewAsync(), FilterFindingKind.Mergeable);
        Gmail.FailNext(HttpStatusCode.BadRequest, 1, afterCalls: 2);

        (await PostAsync($"/api/rules/filters/findings/{finding.Id}/apply")).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var failed = await GetFindingAsync(finding);
        (failed.Status, failed.Error is null).ShouldBe((FilterFindingStatus.Open, false));
        (await Gmail.ListFiltersAsync(Ct)).Count(f => finding.FilterIds.Contains(f.Id)).ShouldBe(1);

        var applied = await ApplyAsync(finding.Id, HttpStatusCode.OK);

        (applied.Status, applied.Error).ShouldBe((FilterFindingStatus.Applied, null));
        var gmail = await Gmail.ListFiltersAsync(Ct);
        gmail.ShouldNotContain(f => finding.FilterIds.Contains(f.Id));
        gmail.Count(f => IsMerged(f)).ShouldBe(1);
    }

    [Fact]
    public async Task A_finding_whose_filter_was_deleted_elsewhere_is_a_conflict()
    {
        var finding = Find(await CoveredReviewAsync(), FilterFindingKind.NoRecentMatches);
        (await Client().DeleteAsync($"/api/rules/filters/{finding.FilterIds[0]}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await PostAsync($"/api/rules/filters/findings/{finding.Id}/apply")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await GetFindingAsync(finding)).Status.ShouldBe(FilterFindingStatus.Open);
    }

    [Fact]
    public async Task Dismiss_closes_an_open_finding_once()
    {
        var finding = Find(await CoveredReviewAsync(), FilterFindingKind.NoRecentMatches);

        var response = await PostAsync($"/api/rules/filters/findings/{finding.Id}/dismiss");

        (await response.Content.ReadFromJsonAsync<FilterFindingDto>(Ct)).ShouldNotBeNull().Status.ShouldBe(FilterFindingStatus.Dismissed);
        (await PostAsync($"/api/rules/filters/findings/{finding.Id}/dismiss")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await PostAsync($"/api/rules/filters/findings/{finding.Id}/apply")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await PostAsync($"/api/rules/filters/findings/{Guid.NewGuid()}/dismiss")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Gmail.ListFiltersAsync(Ct)).ShouldContain(f => f.Id == finding.FilterIds[0]);
    }

    [Fact]
    public async Task A_new_review_supersedes_the_open_findings_of_the_previous_one()
    {
        var first = await CoveredReviewAsync();
        var dismissed = Find(first, FilterFindingKind.NoRecentMatches);
        (await PostAsync($"/api/rules/filters/findings/{dismissed.Id}/dismiss")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await CreateReviewAsync();

        var old = await Client().GetFromJsonAsync<FilterReviewDto>($"/api/rules/filters/reviews/{first.Id}", Ct);
        old.ShouldNotBeNull().Findings.Single(f => f.Id == dismissed.Id).Status.ShouldBe(FilterFindingStatus.Dismissed);
        old.Findings.Where(f => f.Id != dismissed.Id).ShouldAllBe(f => f.Status == FilterFindingStatus.Superseded);
        second.Findings.ShouldAllBe(f => f.Status == FilterFindingStatus.Open);
    }

    [Fact]
    public async Task No_recent_matches_needs_stored_mail_over_the_whole_window_and_a_filter_older_than_it()
    {
        // Fetch not completed: nothing is stale.
        (await CreateReviewAsync()).Findings.ShouldNotContain(f => f.Kind == FilterFindingKind.NoRecentMatches);

        // Fetch completed but the filters were first seen just now.
        await CompleteFetchAsync();
        (await CreateReviewAsync()).Findings.ShouldNotContain(f => f.Kind == FilterFindingKind.NoRecentMatches);

        // Fetch completed, but no stored message is older than the window.
        await using (var db = postgres.CreateDbContext())
        {
            await db.Filters.ExecuteUpdateAsync(s => s.SetProperty(r => r.FirstSeenAt, Now.AddYears(-2)), Ct);
            await db.Messages.Where(m => m.Id == "o0").ExecuteUpdateAsync(s => s.SetProperty(m => m.InternalDate, Now), Ct);
        }

        (await CreateReviewAsync()).Findings.ShouldNotContain(f => f.Kind == FilterFindingKind.NoRecentMatches);
    }

    [Fact]
    public async Task A_half_applied_finding_is_never_superseded_or_dismissed_and_its_filters_are_left_out_of_new_reviews()
    {
        var finding = Find(await CreateReviewAsync(), FilterFindingKind.Mergeable);
        Gmail.FailNext(HttpStatusCode.BadRequest, 1, afterCalls: 2);
        (await PostAsync($"/api/rules/filters/findings/{finding.Id}/apply")).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var next = await CreateReviewAsync();

        await using (var db = postgres.CreateDbContext())
        {
            (await db.FilterFindings.SingleAsync(f => f.Id == finding.Id, Ct)).Status.ShouldBe(FilterFindingStatus.Open);
        }

        var fresh = next.Findings.Where(f => f.ReviewId == next.Id).ToList();
        fresh.ShouldNotContain(f => f.FilterIds.Intersect(finding.FilterIds).Any());
        fresh.ShouldNotContain(f => f.Kind == FilterFindingKind.Mergeable);
        (await PostAsync($"/api/rules/filters/findings/{finding.Id}/dismiss")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ApplyAsync(finding.Id, HttpStatusCode.OK)).Status.ShouldBe(FilterFindingStatus.Applied);
    }

    [Fact]
    public async Task A_re_apply_creates_the_filter_again_when_the_one_it_created_is_gone()
    {
        var finding = Find(await CreateReviewAsync(), FilterFindingKind.Mergeable);
        Gmail.FailNext(HttpStatusCode.BadRequest, 1, afterCalls: 2);
        (await PostAsync($"/api/rules/filters/findings/{finding.Id}/apply")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var created = (await Gmail.ListFiltersAsync(Ct)).Single(f => IsMerged(f));
        (await Client().DeleteAsync($"/api/rules/filters/{created.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ApplyAsync(finding.Id, HttpStatusCode.OK)).Status.ShouldBe(FilterFindingStatus.Applied);

        var gmail = await Gmail.ListFiltersAsync(Ct);
        gmail.ShouldNotContain(f => finding.FilterIds.Contains(f.Id));
        gmail.Where(f => IsMerged(f)).ShouldHaveSingleItem().Id.ShouldNotBe(created.Id);
    }

    [Fact]
    public async Task Latest_carries_over_a_half_applied_finding_of_an_earlier_review_so_it_can_be_resumed()
    {
        var finding = Find(await CreateReviewAsync(), FilterFindingKind.Mergeable);
        Gmail.FailNext(HttpStatusCode.BadRequest, 1, afterCalls: 2);
        (await PostAsync($"/api/rules/filters/findings/{finding.Id}/apply")).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var next = await CreateReviewAsync();
        var latest = await Client().GetFromJsonAsync<FilterReviewDto>("/api/rules/filters/reviews/latest", Ct);

        latest.ShouldNotBeNull().Id.ShouldBe(next.Id);
        var carried = latest.Findings.Single(f => f.Id == finding.Id);
        (carried.ReviewId, carried.Status).ShouldBe((finding.ReviewId, FilterFindingStatus.Open));
        carried.ReviewId.ShouldNotBe(next.Id);
        next.Findings.ShouldContain(f => f.Id == finding.Id);
        (await ApplyAsync(carried.Id, HttpStatusCode.OK)).Status.ShouldBe(FilterFindingStatus.Applied);
        (await Client().GetFromJsonAsync<FilterReviewDto>("/api/rules/filters/reviews/latest", Ct))
            .ShouldNotBeNull().Findings.ShouldNotContain(f => f.Id == finding.Id);
    }

    [Fact]
    public async Task A_failed_re_create_after_the_created_filter_is_gone_keeps_the_finding_half_applied()
    {
        var finding = Find(await CreateReviewAsync(), FilterFindingKind.Mergeable);
        Gmail.FailNext(HttpStatusCode.BadRequest, 1, afterCalls: 2);
        (await PostAsync($"/api/rules/filters/findings/{finding.Id}/apply")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var created = (await Gmail.ListFiltersAsync(Ct)).Single(f => IsMerged(f));
        (await Client().DeleteAsync($"/api/rules/filters/{created.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Gmail.FailNext(HttpStatusCode.BadRequest, 1);
        (await PostAsync($"/api/rules/filters/findings/{finding.Id}/apply")).StatusCode.ShouldNotBe(HttpStatusCode.OK);

        var failed = await GetFindingAsync(finding);
        (failed.Status, failed.Error is null).ShouldBe((FilterFindingStatus.Open, false));
        (await PostAsync($"/api/rules/filters/findings/{finding.Id}/dismiss")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await CreateReviewAsync();
        (await GetFindingAsync(finding)).Status.ShouldBe(FilterFindingStatus.Open);

        (await ApplyAsync(finding.Id, HttpStatusCode.OK)).Status.ShouldBe(FilterFindingStatus.Applied);
        var gmail = await Gmail.ListFiltersAsync(Ct);
        gmail.ShouldNotContain(f => finding.FilterIds.Contains(f.Id));
        gmail.Count(f => IsMerged(f)).ShouldBe(1);
    }

    [Fact]
    public async Task A_merge_applies_at_gmails_filter_limit_since_it_lowers_the_count()
    {
        var finding = Find(await CreateReviewAsync(), FilterFindingKind.Mergeable);
        await using (var db = postgres.CreateDbContext())
        {
            var active = await db.Filters.CountAsync(r => r.DeletedAt == null, Ct);
            db.Filters.AddRange(Enumerable.Range(0, FilterSnapshot.GmailFilterLimit - active).Select(i => new FilterRow
            {
                Id = $"limit-{i}",
                Criteria = FilterRow.WriteCriteria(new GmailFilterCriteria(Subject: $"synthetic {i}")),
                Action = FilterRow.WriteAction(new GmailFilterAction(["Label_1"], [])),
                CriteriaSummary = $"subject:synthetic {i}",
                FirstSeenAt = Now,
            }));
            await db.SaveChangesAsync(Ct);
        }

        (await ApplyAsync(finding.Id, HttpStatusCode.OK)).Status.ShouldBe(FilterFindingStatus.Applied);
    }

    [Fact]
    public async Task Dismiss_and_a_new_review_wait_for_an_apply_in_progress()
    {
        var finding = Find(await CoveredReviewAsync(), FilterFindingKind.NoRecentMatches);
        await using var db = postgres.CreateDbContext();
        await db.Database.OpenConnectionAsync(Ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({FilterReviewService.ApplyLockKey})", Ct);

        var dismiss = PostAsync($"/api/rules/filters/findings/{finding.Id}/dismiss");
        var review = PostAsync("/api/rules/filters/reviews");
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        (dismiss.IsCompleted, review.IsCompleted).ShouldBe((false, false));

        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({FilterReviewService.ApplyLockKey})", Ct);
        (await dismiss).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await review).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>The merge of the two sender filters, whichever order the merge put the senders in.</summary>
    private static bool IsMerged(GmailFilter f) =>
        f.Criteria.From?.Split(" OR ").Order(StringComparer.Ordinal).SequenceEqual([News, Shop]) == true;

    private static FilterFindingDto Find(FilterReviewDto review, FilterFindingKind kind, FilterFixKind? fix = null) =>
        review.Findings.Single(f => f.Kind == kind && (fix is null || f.Fix.Kind == fix));

    private async Task<FilterReviewDto> CreateReviewAsync()
    {
        var response = await PostAsync("/api/rules/filters/reviews");
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<FilterReviewDto>(Ct)).ShouldNotBeNull();
    }

    /// <summary>A review after the first, once the fetch has completed and the filters are older than the window.</summary>
    private async Task<FilterReviewDto> CoveredReviewAsync()
    {
        await CreateReviewAsync();
        await CompleteFetchAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.Filters.ExecuteUpdateAsync(s => s.SetProperty(r => r.FirstSeenAt, Now.AddYears(-2)), Ct);
        }

        return await CreateReviewAsync();
    }

    private async Task CompleteFetchAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.FetchState.ExecuteUpdateAsync(
            s => s.SetProperty(r => r.MailboxPhase, MailboxPhase.Completed).SetProperty(r => r.LastHistoryId, "1"), Ct);
    }

    private async Task<FilterFindingDto> ApplyAsync(Guid id, HttpStatusCode expected)
    {
        var response = await PostAsync($"/api/rules/filters/findings/{id}/apply");
        response.StatusCode.ShouldBe(expected);
        return (await response.Content.ReadFromJsonAsync<FilterFindingDto>(Ct)).ShouldNotBeNull();
    }

    private async Task<FilterFindingDto> GetFindingAsync(FilterFindingDto finding)
    {
        var latest = await Client().GetFromJsonAsync<FilterReviewDto>("/api/rules/filters/reviews/latest", Ct);
        return latest.ShouldNotBeNull().Findings.Single(f => f.Id == finding.Id);
    }

    private static List<FakeMessage> Mailbox() =>
    [
        .. Enumerable.Range(0, 2).Select(i => new FakeMessage($"s{i}", $"t-s{i}", Shop, $"Weekly offer {i + 1}", Now.AddDays(-1 - i), ["INBOX"])),
        .. Enumerable.Range(0, 2).Select(i => new FakeMessage(
            $"n{i}", $"t-n{i}", News, $"Newsletter issue {i + 1}", Now.AddDays(-3 - i), ["INBOX"], To: "lists@example.com")),
        new("o0", "t-o0", "old@example.com", "Synthetic receipt", Now.AddYears(-3), ["INBOX"]),
    ];

    private Task<HttpResponseMessage> PostAsync(string path) => Client().PostAsync(path, null, Ct);

    private HttpClient Client()
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    }
}
