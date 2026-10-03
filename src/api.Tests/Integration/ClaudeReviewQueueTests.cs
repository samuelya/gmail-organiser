using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
using GmailOrganiser.Common;
using GmailOrganiser.Memory;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// The Claude review queue over a finished inbox run of the harness mailbox (shop 10 in one group, news 6, billing 4).
/// Verdicts are submitted through <see cref="ExternalReviewService"/>, as the MCP tool will.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ClaudeReviewQueueTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Path = "/api/claude/reviews";
    private readonly PostgresFixture postgres;
    private readonly CountingStarter starter = new();
    private readonly AnalysisRunHarness h;
    private Guid runId;

    public ClaudeReviewQueueTests(ApiFactory factory, PostgresFixture postgres)
    {
        this.postgres = postgres;
        h = new(factory, postgres) { ConfigureServices = s => s.AddSingleton<IClaudeReviewStarter>(starter) };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.ExternalReviews.ExecuteDeleteAsync(Ct);
        }

        await h.InitializeAsync();
        runId = (await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null))).Id;
        await h.RunNextAsync();
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
    public async Task Create_dedupes_skips_non_pending_and_unknown_targets_and_starts_the_reviewer()
    {
        var shop = await GroupAsync(AnalysisRunHarness.Shop);
        var a01 = await IdAsync("a01");

        var first = await CreateAsync(new([a01, a01, Guid.NewGuid()], [shop, shop with { SenderAddress = shop.SenderAddress.ToUpperInvariant() }], null));

        (first.Created, first.Skipped).ShouldBe((2, 1));
        first.Items.Select(i => (i.TargetType, i.Status, i.Resolution)).ShouldBe([("suggestion", "queued", "none"), ("group", "queued", "none")]);
        first.Items[1].GroupDisplay.ShouldBe("Weekly offer 1");
        first.Items[0].GroupDisplay.ShouldNotBeNullOrWhiteSpace();
        starter.Calls.ShouldBe(1);

        var again = await CreateAsync(new([a01], [shop], null));
        (again.Created, again.Skipped).ShouldBe((0, 2));
        starter.Calls.ShouldBe(1);

        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "b00", SuggestionStatus.Approved);
        }

        (await CreateAsync(new([await IdAsync("b00")], null, null))).Skipped.ShouldBe(1);
    }

    [Fact]
    public async Task Run_expands_to_its_groups_with_pending_members()
    {
        await CreateAsync(new(null, [await GroupAsync(AnalysisRunHarness.Shop)], null));
        await using (var db = postgres.CreateDbContext())
        {
            foreach (var id in await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.News).Select(s => s.MessageId).ToListAsync(Ct))
            {
                await AnalysisRunHarness.DecideAsync(db, id, SuggestionStatus.Rejected);
            }
        }

        int expected;
        await using (var db = postgres.CreateDbContext())
        {
            var pending = db.Suggestions.Where(s => s.Status == SuggestionStatus.Pending && s.SenderAddress != AnalysisRunHarness.Shop);
            expected = await pending.Where(s => s.GroupKey != null).Select(s => new { s.SenderAddress, s.GroupKey }).Distinct().CountAsync(Ct)
                + await pending.CountAsync(s => s.GroupKey == null, Ct);
        }

        var response = await CreateAsync(new(null, null, runId));

        expected.ShouldBeGreaterThan(0);
        (response.Created, response.Skipped).ShouldBe((expected, 1)); // the shop group is already open
        response.Items.ShouldAllBe(i => i.SenderAddress == AnalysisRunHarness.Billing);
        await using var check = postgres.CreateDbContext();
        (await check.ExternalReviews.CountAsync(r => r.RunId == runId, Ct)).ShouldBe(expected);
    }

    [Fact]
    public async Task A_group_named_explicitly_and_by_the_run_keeps_the_run_id()
    {
        var shop = await GroupAsync(AnalysisRunHarness.Shop);

        var response = await CreateAsync(new(null, [shop], runId));

        response.Items.Single(i => i.SenderAddress == shop.SenderAddress && i.GroupKey == shop.GroupKey).ShouldNotBeNull();
        await using var db = postgres.CreateDbContext();
        (await db.ExternalReviews.SingleAsync(r => r.SenderAddress == shop.SenderAddress && r.GroupKey == shop.GroupKey, Ct)).RunId.ShouldBe(runId);
    }

    [Fact]
    public async Task Create_validates_the_request()
    {
        (await h.PostAsync(Path, new CreateExternalReviewsRequest(null, null, null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync(Path, new CreateExternalReviewsRequest([.. Enumerable.Range(0, 201).Select(_ => Guid.NewGuid())], null, null)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync(Path, new CreateExternalReviewsRequest(null, [new GroupRef(" ", "key")], null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var tooMany = await h.PostAsync(Path, new CreateExternalReviewsRequest(
            [.. Enumerable.Range(0, 200).Select(_ => Guid.NewGuid())], [new GroupRef("shop@example.com", "key")], null));
        tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tooMany.Content.ReadAsStringAsync(Ct)).ShouldNotContain("run");
        (await h.PostAsync(Path, new CreateExternalReviewsRequest(null, null, Guid.NewGuid()))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await h.GetAsync($"{Path}?status=nope")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.GetAsync($"{Path}?pageSize=0")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.GetAsync($"{Path}/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Status_transitions_follow_the_table()
    {
        var created = await CreateAsync(new([await IdAsync("a01"), await IdAsync("a02"), await IdAsync("b01")], null, null));
        var (one, two, three) = (created.Items[0].Id, created.Items[1].Id, created.Items[2].Id);

        (await PostOkAsync($"{Path}/{one}/cancel")).Status.ShouldBe("cancelled");
        await PostConflictAsync($"{Path}/{one}/cancel");
        await PostConflictAsync($"{Path}/{one}/dismiss");
        (await PostOkAsync($"{Path}/{one}/retry")).Status.ShouldBe("queued");
        starter.Calls.ShouldBe(2);
        await PostConflictAsync($"{Path}/{one}/retry");

        await using (var scope = h.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ExternalReviewService>();
            var batch = Guid.NewGuid();
            (await service.MarkRunningAsync([two, three], batch, Ct)).ShouldBe(2);
            (await service.MarkRunningAsync([two], batch, Ct)).ShouldBe(0);
            (await service.MarkUnavailableAsync([three], new string('x', 5000), Ct)).ShouldBe(1);
            (await service.SubmitVerdictAsync(three, Agree(), Ct)).Result.ShouldBe(ReviewVerdictResult.Closed);
            (await service.SubmitVerdictAsync(two, Agree(), Ct)).Result.ShouldBe(ReviewVerdictResult.Ok);
            (await service.SubmitVerdictAsync(two, Agree(), Ct)).Result.ShouldBe(ReviewVerdictResult.AlreadyReviewed);
            (await service.SubmitVerdictAsync(Guid.NewGuid(), Agree(), Ct)).Result.ShouldBe(ReviewVerdictResult.NotFound);
        }

        var summary = await GetAsync<ExternalReviewSummaryDto>($"{Path}/summary");
        summary.ShouldBe(new ExternalReviewSummaryDto(1, 0, 1, 1));
        (await GetAsync<ExternalReviewDto>($"{Path}/{three}")).Error!.Length.ShouldBe(ExternalReviewService.MaxErrorLength);

        await PostConflictAsync($"{Path}/{two}/cancel");
        await PostConflictAsync($"{Path}/{two}/retry");
        var dismissed = await PostOkAsync($"{Path}/{two}/dismiss");
        dismissed.Resolution.ShouldBe("dismissed");
        dismissed.ResolvedAt.ShouldNotBeNull();
        await PostConflictAsync($"{Path}/{two}/dismiss");
        await PostConflictAsync($"{Path}/{two}/accept");

        var retried = await PostOkAsync($"{Path}/{three}/retry");
        (retried.Status, retried.Error).ShouldBe(("queued", null));

        var page = await GetAsync<PagedDto<ExternalReviewDto>>($"{Path}?status=queued&pageSize=1");
        (page.Total, page.Items.Count).ShouldBe((2, 1));
        (await GetAsync<PagedDto<ExternalReviewDto>>(Path)).Total.ShouldBe(3);
        (await h.PostAsync($"{Path}/{Guid.NewGuid()}/cancel", new { })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retry_while_the_target_has_another_open_item_is_a_conflict()
    {
        var a01 = await IdAsync("a01");
        var shop = await GroupAsync(AnalysisRunHarness.Shop);
        var first = await CreateAsync(new([a01], [shop], null));
        foreach (var item in first.Items)
        {
            await PostOkAsync($"{Path}/{item.Id}/cancel");
        }

        (await CreateAsync(new([a01], [shop], null))).Created.ShouldBe(2);

        foreach (var item in first.Items)
        {
            await PostConflictAsync($"{Path}/{item.Id}/retry");
            (await GetAsync<ExternalReviewDto>($"{Path}/{item.Id}")).Status.ShouldBe("cancelled");
        }
    }

    [Fact]
    public async Task Submit_validates_and_truncates_the_reasoning()
    {
        var id = (await CreateAsync(new([await IdAsync("a01")], null, null))).Items[0].Id;
        await using var scope = h.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ExternalReviewService>();

        (await service.SubmitVerdictAsync(id, Alternative(null), Ct)).Result.ShouldBe(ReviewVerdictResult.Invalid);
        (await service.SubmitVerdictAsync(id, Alternative("INBOX"), Ct)).Result.ShouldBe(ReviewVerdictResult.Invalid);
        (await service.SubmitVerdictAsync(id, Agree() with { Reviewer = "someone" }, Ct)).Result.ShouldBe(ReviewVerdictResult.Invalid);
        (await service.SubmitVerdictAsync(id, Agree() with { FilterCriteria = "{not json" }, Ct)).Result.ShouldBe(ReviewVerdictResult.Invalid);
        var (result, reason) = await service.SubmitVerdictAsync(id, Agree() with { Reasoning = " " }, Ct);
        (result, reason).ShouldBe((ReviewVerdictResult.Invalid, "Reasoning is required."));

        var ok = Alternative("Shopping/Receipts") with { Reasoning = new string('r', 5000), FilterCriteria = "{\"from\":\"shop@example.com\"}" };
        (await service.SubmitVerdictAsync(id, ok, Ct)).Result.ShouldBe(ReviewVerdictResult.Ok);

        var item = await GetAsync<ExternalReviewDto>($"{Path}/{id}");
        (item.Status, item.Verdict, item.VerdictTopicLabel, item.VerdictNeedsAction, item.VerdictToBeDeleted, item.Reviewer)
            .ShouldBe(("reviewed", "alternative", "Shopping/Receipts", false, false, "mcp"));
        item.Reasoning!.Length.ShouldBe(ExternalReviewService.MaxReasoningLength);
    }

    [Fact]
    public async Task Accept_agree_approves_the_suggestion_and_writes_its_decision()
    {
        var suggestion = await IdAsync("a01");
        var id = await ReviewedAsync(new([suggestion], null, null), Agree());

        var accepted = await PostOkAsync($"{Path}/{id}/accept");

        (accepted.Resolution, accepted.ResolvedAt.HasValue).ShouldBe(("accepted_claude", true));
        await using var db = postgres.CreateDbContext();
        var row = await db.Suggestions.AsNoTracking().SingleAsync(s => s.Id == suggestion, Ct);
        (row.Status, row.Edited, row.TopicLabel).ShouldBe((SuggestionStatus.Approved, false, "Shopping"));
        var decision = await db.Decisions.AsNoTracking().SingleAsync(Ct);
        (decision.MessageId, decision.Outcome, decision.TopicLabel, decision.Edited).ShouldBe(("a01", DecisionOutcome.Approved, "Shopping", false));
        await PostConflictAsync($"{Path}/{id}/accept");
    }

    [Fact]
    public async Task Accept_alternative_edits_and_approves_the_suggestion()
    {
        var suggestion = await IdAsync("a01");
        var id = await ReviewedAsync(new([suggestion], null, null), Alternative("Shopping/Receipts", needsAction: true));

        await PostOkAsync($"{Path}/{id}/accept");

        await using var db = postgres.CreateDbContext();
        var row = await db.Suggestions.AsNoTracking().SingleAsync(s => s.Id == suggestion, Ct);
        (row.Status, row.Edited, row.TopicLabel, row.NeedsAction).ShouldBe((SuggestionStatus.Approved, true, "Shopping/Receipts", true));
        var decision = await db.Decisions.AsNoTracking().SingleAsync(Ct);
        (decision.Outcome, decision.TopicLabel, decision.NeedsAction, decision.Edited).ShouldBe((DecisionOutcome.Approved, "Shopping/Receipts", true, true));
    }

    [Fact]
    public async Task Accept_needs_human_or_an_already_decided_suggestion_is_a_conflict()
    {
        var needsHuman = await ReviewedAsync(new([await IdAsync("a01")], null, null), Agree() with { Verdict = ReviewVerdict.NeedsHuman });
        var decided = await ReviewedAsync(new([await IdAsync("a02")], null, null), Agree());
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "a02", SuggestionStatus.Rejected);
        }

        (await PostConflictAsync($"{Path}/{needsHuman}/accept")).ShouldContain("Needs a human");
        (await PostConflictAsync($"{Path}/{decided}/accept")).ShouldContain("already decided", Case.Insensitive);

        await using var check = postgres.CreateDbContext();
        (await check.Decisions.CountAsync(Ct)).ShouldBe(0);
        (await check.ExternalReviews.CountAsync(r => r.Resolution != ExternalReviewResolution.None, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Accept_agree_on_a_group_approves_the_members_with_the_card_outcome()
    {
        var shop = await GroupAsync(AnalysisRunHarness.Shop);
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "a09", SuggestionStatus.Pending, s => s.TopicLabel = "Other");
        }

        var id = await ReviewedAsync(new(null, [shop], null), Agree());
        await PostOkAsync($"{Path}/{id}/accept");

        await using var check = postgres.CreateDbContext();
        var statuses = await check.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Shop)
            .ToDictionaryAsync(s => s.MessageId, s => s.Status, Ct);
        statuses["a09"].ShouldBe(SuggestionStatus.Pending);
        statuses.Where(p => p.Key != "a09").ShouldAllBe(p => p.Value == SuggestionStatus.Approved);
        (await check.Decisions.CountAsync(d => d.Outcome == DecisionOutcome.Approved && d.TopicLabel == "Shopping", Ct)).ShouldBe(9);
    }

    [Fact]
    public async Task Accept_alternative_on_a_group_edits_every_pending_member_but_a_protected_deletion()
    {
        var shop = await GroupAsync(AnalysisRunHarness.Shop);
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => m.Id == "a03").ExecuteUpdateAsync(s => s.SetProperty(m => m.HasAttachment, true), Ct);
            await AnalysisRunHarness.DecideAsync(db, "a09", SuggestionStatus.Pending, s => s.TopicLabel = "Other");
        }

        var id = await ReviewedAsync(new(null, [shop], null), Alternative("Shopping/Ads", toBeDeleted: true));
        await PostOkAsync($"{Path}/{id}/accept");

        await using var check = postgres.CreateDbContext();
        var rows = await check.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Shop).ToListAsync(Ct);
        var protectedRow = rows.Single(s => s.MessageId == "a03");
        (protectedRow.Status, protectedRow.ToBeDeleted, protectedRow.Edited).ShouldBe((SuggestionStatus.Pending, false, false));
        rows.Where(s => s.MessageId != "a03").ShouldAllBe(s =>
            s.Status == SuggestionStatus.Approved && s.Edited && s.TopicLabel == "Shopping/Ads" && s.ToBeDeleted);
        (await check.Decisions.CountAsync(d => d.TopicLabel == "Shopping/Ads" && d.ToBeDeleted && d.Edited, Ct)).ShouldBe(9);
        (await check.ExternalReviews.SingleAsync(r => r.Id == id, Ct)).Resolution.ShouldBe(ExternalReviewResolution.AcceptedClaude);
    }

    [Fact]
    public async Task Accept_alternative_never_marks_a_protected_suggestion_to_be_deleted()
    {
        var suggestion = await IdAsync("a03");
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => m.Id == "a03").ExecuteUpdateAsync(s => s.SetProperty(m => m.HasAttachment, true), Ct);
        }

        var id = await ReviewedAsync(new([suggestion], null, null), Alternative("Shopping/Ads", toBeDeleted: true));

        (await PostConflictAsync($"{Path}/{id}/accept")).ShouldContain("Not applicable");
        await using var check = postgres.CreateDbContext();
        var row = await check.Suggestions.AsNoTracking().SingleAsync(s => s.Id == suggestion, Ct);
        (row.Status, row.ToBeDeleted, row.Edited).ShouldBe((SuggestionStatus.Pending, false, false));
        (await check.Decisions.CountAsync(Ct)).ShouldBe(0);
        (await check.ExternalReviews.SingleAsync(r => r.Id == id, Ct)).Resolution.ShouldBe(ExternalReviewResolution.None);
    }

    [Fact]
    public async Task Accept_on_a_group_with_nothing_pending_is_already_decided_and_stays_open()
    {
        var shop = await GroupAsync(AnalysisRunHarness.Shop);
        var id = await ReviewedAsync(new(null, [shop], null), Alternative("Shopping/Ads"));
        await using (var db = postgres.CreateDbContext())
        {
            foreach (var message in await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Shop).Select(s => s.MessageId).ToListAsync(Ct))
            {
                await AnalysisRunHarness.DecideAsync(db, message, SuggestionStatus.Rejected);
            }
        }

        (await PostConflictAsync($"{Path}/{id}/accept")).ShouldContain("already decided", Case.Insensitive);
        await using var check = postgres.CreateDbContext();
        (await check.Decisions.CountAsync(Ct)).ShouldBe(0);
        (await check.ExternalReviews.SingleAsync(r => r.Id == id, Ct)).Resolution.ShouldBe(ExternalReviewResolution.None);
        (await PostOkAsync($"{Path}/{id}/dismiss")).Resolution.ShouldBe("dismissed");
    }

    [Fact]
    public async Task Concurrent_accepts_and_a_dismiss_decide_once()
    {
        var suggestion = await IdAsync("a01");
        var id = await ReviewedAsync(new([suggestion], null, null), Alternative("Shopping/Receipts"));

        var responses = await Task.WhenAll(
            h.PostAsync($"{Path}/{id}/accept", new { }), h.PostAsync($"{Path}/{id}/accept", new { }), h.PostAsync($"{Path}/{id}/dismiss", new { }));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(2);
        await using var db = postgres.CreateDbContext();
        var item = await db.ExternalReviews.SingleAsync(r => r.Id == id, Ct);
        var decisions = await db.Decisions.CountAsync(Ct);
        if (responses[2].StatusCode == HttpStatusCode.OK)
        {
            (item.Resolution, decisions).ShouldBe((ExternalReviewResolution.Dismissed, 0));
            (await db.Suggestions.SingleAsync(s => s.Id == suggestion, Ct)).Status.ShouldBe(SuggestionStatus.Pending);
        }
        else
        {
            (item.Resolution, decisions).ShouldBe((ExternalReviewResolution.AcceptedClaude, 1));
        }
    }

    [Fact]
    public async Task Accept_agree_on_a_group_approves_the_outcome_claude_reviewed_not_the_current_card()
    {
        var shop = await GroupAsync(AnalysisRunHarness.Shop);
        var id = await ReviewedAsync(new(null, [shop], null), Agree());
        (await GetAsync<ExternalReviewDto>($"{Path}/{id}")).VerdictTopicLabel.ShouldBe("Shopping");

        // The group changes after the review: six members now show another outcome, so the card does too.
        await using (var db = postgres.CreateDbContext())
        {
            foreach (var message in new[] { "a00", "a01", "a02", "a03", "a04", "a05" })
            {
                await AnalysisRunHarness.DecideAsync(db, message, SuggestionStatus.Pending, s => s.TopicLabel = "Other");
            }
        }

        await PostOkAsync($"{Path}/{id}/accept");

        await using var check = postgres.CreateDbContext();
        var rows = await check.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Shop).ToListAsync(Ct);
        rows.Where(s => s.TopicLabel == "Other").ShouldAllBe(s => s.Status == SuggestionStatus.Pending);
        rows.Where(s => s.TopicLabel == "Shopping").ShouldAllBe(s => s.Status == SuggestionStatus.Approved);
        (await check.Decisions.CountAsync(Ct)).ShouldBe(4);
    }

    private static ReviewVerdictInput Agree() => new(ReviewVerdict.Agree, null, null, null, null, "Synthetic reasoning", "mcp", null);

    private static ReviewVerdictInput Alternative(string? label, bool needsAction = false, bool toBeDeleted = false) =>
        new(ReviewVerdict.Alternative, label, needsAction, toBeDeleted, null, "Synthetic reasoning", "mcp", null);

    /// <summary>Creates one item and submits <paramref name="verdict"/> for it.</summary>
    private async Task<Guid> ReviewedAsync(CreateExternalReviewsRequest request, ReviewVerdictInput verdict)
    {
        var id = (await CreateAsync(request)).Items.ShouldHaveSingleItem().Id;
        await using var scope = h.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<ExternalReviewService>().SubmitVerdictAsync(id, verdict, Ct)).Result
            .ShouldBe(ReviewVerdictResult.Ok);
        return id;
    }

    private async Task<GroupRef> GroupAsync(string sender)
    {
        await using var db = postgres.CreateDbContext();
        var key = await db.Suggestions.Where(s => s.SenderAddress == sender && s.GroupKey != null).Select(s => s.GroupKey!).Distinct().SingleAsync(Ct);
        return new GroupRef(sender, key);
    }

    private async Task<Guid> IdAsync(string messageId)
    {
        await using var db = postgres.CreateDbContext();
        return await db.Suggestions.Where(s => s.MessageId == messageId).Select(s => s.Id).SingleAsync(Ct);
    }

    private async Task<CreateExternalReviewsResponse> CreateAsync(CreateExternalReviewsRequest request)
    {
        var response = await h.PostAsync(Path, request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CreateExternalReviewsResponse>(Ct)).ShouldNotBeNull();
    }

    private async Task<ExternalReviewDto> PostOkAsync(string path)
    {
        var response = await h.PostAsync(path, new { });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ExternalReviewDto>(Ct)).ShouldNotBeNull();
    }

    /// <summary>Asserts 409 and returns the problem body.</summary>
    private async Task<string> PostConflictAsync(string path)
    {
        var response = await h.PostAsync(path, new { });
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body);
        return body;
    }

    private async Task<T> GetAsync<T>(string path)
        where T : class
    {
        var response = await h.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct)).ShouldNotBeNull();
    }

    private sealed class CountingStarter : IClaudeReviewStarter
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public Task StartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }
    }
}
