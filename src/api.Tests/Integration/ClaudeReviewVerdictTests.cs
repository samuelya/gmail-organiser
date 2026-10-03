using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// Claude's verdict beside the local suggestion on the review page, the "suggested for Claude" hint and the item events,
/// over a finished inbox run of the harness mailbox (shop 10 in one group: 3 model at 0.9, 7 derived at 0.8).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ClaudeReviewVerdictTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Path = "/api/claude/reviews";
    private readonly PostgresFixture postgres;
    private readonly CapturingNotifier notifier = new();
    private readonly AnalysisRunHarness h;

    public ClaudeReviewVerdictTests(ApiFactory factory, PostgresFixture postgres)
    {
        this.postgres = postgres;
        h = new(factory, postgres)
        {
            ConfigureServices = s => s.AddSingleton<IExternalReviewNotifier>(notifier),
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.ExternalReviews.ExecuteDeleteAsync(Ct);
        }

        await h.InitializeAsync();
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsNewLabel, false), Ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.ExternalReviews.ExecuteDeleteAsync(Ct);
        }

        await h.DisposeAsync();
    }

    [Fact]
    public async Task Detail_shows_the_newest_not_cancelled_item_beside_the_suggestion_and_the_group()
    {
        var a01 = await IdAsync("a01");
        var shop = await GroupAsync();

        var first = await CreateOneAsync(new([a01], null, null));
        await SubmitAsync(first);
        await PostOkAsync($"{Path}/{first}/dismiss");
        var second = await CreateOneAsync(new([a01], null, null));
        var groupItem = await CreateOneAsync(new(null, [shop], null));

        var group = (await DetailAsync()).Groups.ShouldHaveSingleItem();
        var item = group.Members.Single(m => m.Id == a01).ClaudeReview.ShouldNotBeNull();
        (item.Id, item.TargetType, item.Status).ShouldBe((second, "suggestion", "queued"));
        item.GroupDisplay.ShouldNotBeNullOrWhiteSpace();
        group.Members.Where(m => m.Id != a01).ShouldAllBe(m => m.ClaudeReview == null);
        var shown = group.ClaudeReview.ShouldNotBeNull();
        (shown.Id, shown.TargetType, shown.GroupDisplay).ShouldBe((groupItem, "group", group.Display));

        await PostOkAsync($"{Path}/{second}/cancel");
        await PostOkAsync($"{Path}/{groupItem}/cancel");

        group = (await DetailAsync()).Groups.ShouldHaveSingleItem();
        var older = group.Members.Single(m => m.Id == a01).ClaudeReview.ShouldNotBeNull();
        (older.Id, older.Status, older.Verdict, older.Resolution).ShouldBe((first, "reviewed", "agree", "dismissed"));
        group.ClaudeReview.ShouldBeNull();
    }

    [Fact]
    public async Task Suggested_for_claude_needs_the_reviewer_on_and_a_rule_that_matches()
    {
        await SettingsAsync(ClaudeReviewerMode.Off, lowConfidence: true, threshold: 0.85, newLabels: true);
        var group = (await DetailAsync()).Groups.Single();
        group.SuggestedForClaude.ShouldBeFalse();
        group.Members.ShouldAllBe(m => !m.SuggestedForClaude);

        // Low confidence: the derived members (0.8) are below the threshold, the model answers (0.9) are not.
        await SettingsAsync(ClaudeReviewerMode.HeadlessClaudeCode, lowConfidence: true, threshold: 0.85, newLabels: false);
        group = (await DetailAsync()).Groups.Single();
        group.SuggestedForClaude.ShouldBeTrue();
        group.Members.ShouldAllBe(m => m.SuggestedForClaude == (m.Source == "derived"));

        await SettingsAsync(ClaudeReviewerMode.ClaudeDesktop, lowConfidence: true, threshold: 0.6, newLabels: true);
        group = (await DetailAsync()).Groups.Single();
        group.SuggestedForClaude.ShouldBeFalse();
        group.Members.ShouldAllBe(m => !m.SuggestedForClaude);

        // A new label on one member marks it and its group.
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.MessageId == "a09").ExecuteUpdateAsync(s => s.SetProperty(x => x.IsNewLabel, true), Ct);
        }

        group = (await DetailAsync()).Groups.Single();
        group.SuggestedForClaude.ShouldBeTrue();
        group.Members.Where(m => m.SuggestedForClaude).ShouldHaveSingleItem().MessageId.ShouldBe("a09");

        await SettingsAsync(ClaudeReviewerMode.ClaudeDesktop, lowConfidence: true, threshold: 0.6, newLabels: false);
        (await DetailAsync()).Groups.Single().SuggestedForClaude.ShouldBeFalse();
    }

    [Fact]
    public async Task Every_committed_status_or_resolution_change_is_published()
    {
        var a01 = await IdAsync("a01");
        var a02 = await IdAsync("a02");
        var item = await CreateOneAsync(new([a01], null, null));
        Events().ShouldBe([(item, "queued", "none")]);

        await PostOkAsync($"{Path}/{item}/cancel");
        (await h.PostAsync($"{Path}/{item}/cancel", new { })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await PostOkAsync($"{Path}/{item}/retry");
        Events().ShouldBe([(item, "cancelled", "none"), (item, "queued", "none")]);

        await using (var scope = h.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ExternalReviewService>();
            (await service.MarkRunningAsync([item], Guid.NewGuid(), Ct)).ShouldBe(1);
            (await service.MarkRunningAsync([item], Guid.NewGuid(), Ct)).ShouldBe(0);
            (await service.MarkUnavailableAsync([item], "Synthetic failure", Ct)).ShouldBe(1);
        }

        Events().ShouldBe([(item, "running", "none"), (item, "unavailable", "none")]);

        await PostOkAsync($"{Path}/{item}/retry");
        await SubmitAsync(item);
        await PostOkAsync($"{Path}/{item}/dismiss");
        Events().ShouldBe([(item, "queued", "none"), (item, "reviewed", "none"), (item, "reviewed", "dismissed")]);

        var other = await CreateOneAsync(new([a02], null, null));
        await SubmitAsync(other);
        await PostOkAsync($"{Path}/{other}/accept");
        (await h.PostAsync($"{Path}/{other}/accept", new { })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        Events().ShouldBe([(other, "queued", "none"), (other, "reviewed", "none"), (other, "reviewed", "accepted_claude")]);
    }

    /// <summary>The events since the last call, as (id, status, resolution).</summary>
    private List<(Guid, string, string)> Events()
    {
        var events = new List<(Guid, string, string)>();
        while (notifier.Items.TryDequeue(out var e))
        {
            events.Add((e.Id, e.Status, e.Resolution));
        }

        return events;
    }

    private async Task SettingsAsync(ClaudeReviewerMode mode, bool lowConfidence, double threshold, bool newLabels)
    {
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(s => s with
        {
            ClaudeReviewerMode = mode,
            ClaudeSuggestLowConfidence = lowConfidence,
            ClaudeSuggestThreshold = threshold,
            ClaudeSuggestNewLabels = newLabels,
        }, Ct);
    }

    private async Task SubmitAsync(Guid id)
    {
        await using var scope = h.Services.CreateAsyncScope();
        var verdict = new ReviewVerdictInput(ReviewVerdict.Agree, null, null, null, null, "Synthetic reasoning", "mcp", null);
        (await scope.ServiceProvider.GetRequiredService<ExternalReviewService>().SubmitVerdictAsync(id, verdict, Ct)).Result
            .ShouldBe(ReviewVerdictResult.Ok);
    }

    private async Task<Guid> CreateOneAsync(CreateExternalReviewsRequest request)
    {
        var response = await h.PostAsync(Path, request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CreateExternalReviewsResponse>(Ct)).ShouldNotBeNull().Items.ShouldHaveSingleItem().Id;
    }

    private async Task PostOkAsync(string path)
    {
        var response = await h.PostAsync(path, new { });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<ReviewSenderDetailDto> DetailAsync()
    {
        var response = await h.GetAsync($"/api/review/senders/{AnalysisRunHarness.Shop}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ReviewSenderDetailDto>(Ct)).ShouldNotBeNull();
    }

    private async Task<GroupRef> GroupAsync()
    {
        await using var db = postgres.CreateDbContext();
        var key = await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Shop).Select(s => s.GroupKey!).Distinct().SingleAsync(Ct);
        return new GroupRef(AnalysisRunHarness.Shop, key);
    }

    private async Task<Guid> IdAsync(string messageId)
    {
        await using var db = postgres.CreateDbContext();
        return await db.Suggestions.Where(s => s.MessageId == messageId).Select(s => s.Id).SingleAsync(Ct);
    }

    /// <summary>Sends nothing; keeps every published item in order.</summary>
    private sealed class CapturingNotifier : IExternalReviewNotifier
    {
        public ConcurrentQueue<ExternalReviewDto> Items { get; } = new();

        public Task NotifyAsync(ExternalReviewDto item, CancellationToken ct)
        {
            Items.Enqueue(item);
            return Task.CompletedTask;
        }
    }
}
