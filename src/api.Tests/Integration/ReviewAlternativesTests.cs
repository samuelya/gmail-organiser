using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Review alternatives (#249): old vs new on the review DTOs, accept and discard.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReviewAlternativesTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Compared = "Compared";
    private readonly AnalysisRunHarness h = new(factory, postgres);
    private readonly Guid batchId = Guid.CreateVersion7();
    private AnalysisRunDto compare = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A normal run writes 20 suggestions; a compare run over it stores an alternative for each.</summary>
    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Agree(ids));
        var first = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();

        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Answer(ids, (_, _) => Compared));
        var response = await h.PostAsync("/api/analysis/compare-runs", new CompareRunRequest(null, first.Id));
        compare = (await response.Content.ReadFromJsonAsync<AnalysisRunDto>(Ct)).ShouldNotBeNull();
        await h.RunNextAsync();
        await using var db = postgres.CreateDbContext();
        (await db.SuggestionAlternatives.CountAsync(Ct)).ShouldBe(20);
    }

    public async ValueTask DisposeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.ExternalReviews.ExecuteDeleteAsync(Ct);
            await db.ActionLog.Where(l => l.BatchId == batchId).ExecuteDeleteAsync(Ct);
            await db.ActionBatches.Where(b => b.Id == batchId).ExecuteDeleteAsync(Ct);
        }

        await h.DisposeAsync();
    }

    [Fact]
    public async Task Members_and_groups_show_the_alternative_and_a_group_whose_members_disagree_is_mixed()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.SuggestionAlternatives.Where(a => a.MessageId == "b00").ExecuteUpdateAsync(s => s.SetProperty(a => a.TopicLabel, "Other"), Ct);
        }

        var shop = await DetailAsync(AnalysisRunHarness.Shop);
        var group = shop.Groups.ShouldHaveSingleItem();
        group.TopicLabel.ShouldBe("Shopping");
        var alternative = group.Alternative.ShouldNotBeNull();
        (alternative.TopicLabel, alternative.Mixed, alternative.Count, alternative.LabelChange).ShouldBe((Compared, false, 10, LabelChange.None));
        alternative.Model.ShouldBe(AnalysisRunHarness.ChatModel);
        alternative.PromptVersion.ShouldNotBeNullOrEmpty();
        group.Members.ShouldAllBe(m => m.Alternative!.TopicLabel == Compared && m.TopicLabel == "Shopping");

        var news = (await DetailAsync(AnalysisRunHarness.News)).Groups.ShouldHaveSingleItem().Alternative.ShouldNotBeNull();
        (news.TopicLabel, news.Mixed, news.Count).ShouldBe((Compared, true, 6));
    }

    [Fact]
    public async Task Filter_and_summary_count_only_suggestions_with_an_alternative()
    {
        var discard = await DecideAsync("discard", new AlternativeDecisionRequest(null, [new GroupRef(AnalysisRunHarness.Billing, await GroupKeyAsync("c00"))]));
        discard.ShouldBe(new AlternativeDecisionResponse(0, 4, 0));

        var all = await h.GetAsync("/api/review/senders");
        (await all.Content.ReadFromJsonAsync<PagedDto<ReviewSenderDto>>(Ct))!.Items.ShouldContain(s => s.Address == AnalysisRunHarness.Billing);
        var filtered = await (await h.GetAsync("/api/review/senders?hasAlternative=true")).Content.ReadFromJsonAsync<PagedDto<ReviewSenderDto>>(Ct);
        filtered!.Items.Select(s => s.Address).ShouldBe([AnalysisRunHarness.Shop, AnalysisRunHarness.News]);
        (await h.GetAsync($"/api/review/senders/{AnalysisRunHarness.Billing}?hasAlternative=true")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await DetailAsync(AnalysisRunHarness.Billing)).Groups.ShouldHaveSingleItem().Alternative.ShouldBeNull();

        var summary = await (await h.GetAsync("/api/analysis/summary")).Content.ReadFromJsonAsync<AnalysisSummaryDto>(Ct);
        summary!.Alternatives.ShouldBe(16);
        await using var db = postgres.CreateDbContext();
        (await db.Suggestions.CountAsync(s => s.MessageId.StartsWith("c"), Ct)).ShouldBe(4);
    }

    [Fact]
    public async Task Accept_on_pending_approved_and_applied_yields_pending_suggestions_with_the_alternative_and_records_no_decision()
    {
        int decisions;
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "a00", SuggestionStatus.Pending, s => s.Edited = true);
            await AnalysisRunHarness.DecideAsync(db, "a01", SuggestionStatus.Approved);
            await AnalysisRunHarness.DecideAsync(db, "a02", SuggestionStatus.Applied);
            decisions = await db.Decisions.CountAsync(Ct);
        }

        var ids = await IdsAsync("a00", "a01", "a02");
        (await DecideAsync("accept", new AlternativeDecisionRequest(ids, null))).ShouldBe(new AlternativeDecisionResponse(3, 0, 0));

        await using var check = postgres.CreateDbContext();
        var accepted = await check.Suggestions.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(Ct);
        accepted.ShouldAllBe(s => s.Status == SuggestionStatus.Pending && s.DecidedAt == null && !s.Edited
            && s.TopicLabel == Compared && s.RunId == compare.Id && s.Model == AnalysisRunHarness.ChatModel);
        (await check.Messages.Where(m => m.Id == "a00" || m.Id == "a01" || m.Id == "a02").Select(m => m.AnalysisStatus).ToListAsync(Ct))
            .ShouldAllBe(s => s == AnalysisStatus.Analysed);
        (await check.SuggestionAlternatives.CountAsync(a => ids.Contains(a.SuggestionId), Ct)).ShouldBe(0);
        (await check.SuggestionAlternatives.CountAsync(Ct)).ShouldBe(17);
        (await check.Decisions.CountAsync(Ct)).ShouldBe(decisions);

        // Already accepted: nothing left to accept or discard.
        (await DecideAsync("accept", new AlternativeDecisionRequest(ids, null))).ShouldBe(new AlternativeDecisionResponse(0, 0, 0));
        (await DecideAsync("discard", new AlternativeDecisionRequest(ids, null))).ShouldBe(new AlternativeDecisionResponse(0, 0, 0));
    }

    [Fact]
    public async Task Applied_suggestions_with_an_alternative_are_listed_and_accepted_by_group()
    {
        var shopIds = Enumerable.Range(0, 10).Select(i => $"a{i:00}").ToArray();
        await using (var db = postgres.CreateDbContext())
        {
            foreach (var id in shopIds)
            {
                await AnalysisRunHarness.DecideAsync(db, id, SuggestionStatus.Applied);
            }
        }

        var senders = await (await h.GetAsync("/api/review/senders?status=applied&hasAlternative=true")).Content.ReadFromJsonAsync<PagedDto<ReviewSenderDto>>(Ct);
        var shop = senders!.Items.ShouldHaveSingleItem();
        (shop.Address, shop.Applied, senders.Total).ShouldBe((AnalysisRunHarness.Shop, 10, 1L));
        (await h.GetAsync("/api/review/senders?status=APPLIED")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await h.GetAsync($"/api/review/senders/{AnalysisRunHarness.Shop}?status=applied&hasAlternative=true");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var group = (await response.Content.ReadFromJsonAsync<ReviewSenderDetailDto>(Ct))!.Groups.ShouldHaveSingleItem();
        (group.Size, group.Alternative!.Count).ShouldBe((10, 10));
        (await DetailAsync(AnalysisRunHarness.Shop)).Groups.ShouldBeEmpty();

        var groupRef = new GroupRef(AnalysisRunHarness.Shop, group.GroupKey!, "applied");
        (await DecideAsync("accept", new AlternativeDecisionRequest(null, [groupRef]))).ShouldBe(new AlternativeDecisionResponse(10, 0, 0));
        await using var check = postgres.CreateDbContext();
        (await check.Suggestions.CountAsync(s => shopIds.Contains(s.MessageId) && s.Status == SuggestionStatus.Pending && s.TopicLabel == Compared, Ct))
            .ShouldBe(10);
    }

    [Fact]
    public async Task Review_decisions_still_refuse_applied_suggestions()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "a00", SuggestionStatus.Applied);
            await AnalysisRunHarness.DecideAsync(db, "a01", SuggestionStatus.Applied);
        }

        var ids = await IdsAsync("a00", "a01");
        (await h.PostWithoutBodyAsync($"/api/review/suggestions/{ids[0]}/approve")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await h.PostWithoutBodyAsync($"/api/review/suggestions/{ids[0]}/reject")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await h.PutAsync($"/api/review/suggestions/{ids[0]}", new EditSuggestionRequest("Synthetic", false, false))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await h.PostAsync("/api/review/analyse-individually", new AnalyseIndividuallyRequest(ids))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var reject = await h.PostAsync("/api/review/groups/reject", new GroupDecisionRequest(AnalysisRunHarness.Shop, await GroupKeyAsync("a00")));
        (await reject.Content.ReadFromJsonAsync<GroupDecisionResponse>(Ct))!.Changed.ShouldBe(8);

        await using var check = postgres.CreateDbContext();
        (await check.Suggestions.CountAsync(s => ids.Contains(s.Id) && s.Status == SuggestionStatus.Applied, Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Discard_only_deletes_the_alternative()
    {
        var ids = await IdsAsync("b00");
        (await DecideAsync("discard", new AlternativeDecisionRequest(ids, null))).ShouldBe(new AlternativeDecisionResponse(0, 1, 0));

        await using var check = postgres.CreateDbContext();
        var suggestion = await check.Suggestions.AsNoTracking().SingleAsync(s => s.Id == ids[0], Ct);
        (suggestion.TopicLabel, suggestion.Status).ShouldBe(("News", SuggestionStatus.Pending));
        (await check.SuggestionAlternatives.CountAsync(Ct)).ShouldBe(19);
    }

    [Fact]
    public async Task Suggestions_in_an_active_apply_batch_are_skipped_and_counted()
    {
        var now = DateTimeOffset.UtcNow;
        var ids = await IdsAsync("a03", "a04", "a05");
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "a03", SuggestionStatus.Approved);
            await AnalysisRunHarness.DecideAsync(db, "a04", SuggestionStatus.Applied);
            await AnalysisRunHarness.DecideAsync(db, "a05", SuggestionStatus.Approved);
            db.ActionBatches.Add(new ActionBatchRow { Id = batchId, Kind = ActionKind.Apply, Description = "Synthetic", CreatedAt = now });
            db.ActionLog.Add(new ActionLogRow { Id = Guid.CreateVersion7(), BatchId = batchId, MessageId = "a04", SuggestionId = ids[1], CreatedAt = now });
            // a05 is approved after the batch's cutoff, so the batch does not cover it.
            var cursor = new ApplyCursor(batchId, now.AddSeconds(5), 2, AnalysisRunHarness.Shop, null);
            db.Jobs.Add(new JobRow
            {
                Id = Guid.CreateVersion7(),
                Type = ApplyActionsJob.JobType,
                Queue = JobQueues.Apply,
                DedupKey = batchId.ToString(),
                Status = JobStatus.Running,
                Cursor = JsonSerializer.Serialize(cursor, JsonSerializerOptions.Web),
                CreatedAt = now,
                QueuedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(Ct);
            await db.Suggestions.Where(s => s.MessageId == "a05").ExecuteUpdateAsync(s => s.SetProperty(x => x.DecidedAt, now.AddMinutes(1)), Ct);
        }

        (await DecideAsync("accept", new AlternativeDecisionRequest(ids, null))).ShouldBe(new AlternativeDecisionResponse(1, 0, 2));

        await using var check = postgres.CreateDbContext();
        var rows = await check.Suggestions.AsNoTracking().Where(s => ids.Contains(s.Id)).OrderBy(s => s.MessageId).ToListAsync(Ct);
        rows.Select(s => (s.MessageId, s.Status, s.TopicLabel)).ShouldBe([
            ("a03", SuggestionStatus.Approved, "Shopping"),
            ("a04", SuggestionStatus.Applied, "Shopping"),
            ("a05", SuggestionStatus.Pending, Compared),
        ]);
        (await check.SuggestionAlternatives.CountAsync(a => ids.Contains(a.SuggestionId), Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Group_accept_takes_only_the_members_in_the_status_the_card_was_listed_in()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "a00", SuggestionStatus.Approved);
            await AnalysisRunHarness.DecideAsync(db, "a01", SuggestionStatus.Approved);
            await AnalysisRunHarness.DecideAsync(db, "a02", SuggestionStatus.Applied);
        }

        var key = await GroupKeyAsync("a03");
        var card = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.ShouldHaveSingleItem().Alternative.ShouldNotBeNull();
        card.Count.ShouldBe(7);
        (await DecideAsync("accept", new AlternativeDecisionRequest(null, [new GroupRef(AnalysisRunHarness.Shop, key, "pending")])))
            .ShouldBe(new AlternativeDecisionResponse(card.Count, 0, 0));
        (await DecideAsync("discard", new AlternativeDecisionRequest(null, [new GroupRef(AnalysisRunHarness.Shop, key, "approved")])))
            .ShouldBe(new AlternativeDecisionResponse(0, 2, 0));

        await using var check = postgres.CreateDbContext();
        var rows = await check.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Shop).ToListAsync(Ct);
        rows.Where(s => s.TopicLabel == Compared).ShouldAllBe(s => s.Status == SuggestionStatus.Pending);
        rows.Count(s => s.TopicLabel == Compared).ShouldBe(7);
        rows.Where(s => s.MessageId == "a00" || s.MessageId == "a01").ShouldAllBe(s => s.Status == SuggestionStatus.Approved && s.TopicLabel == "Shopping");
        (await check.SuggestionAlternatives.SingleAsync(a => a.MessageId.StartsWith("a"), Ct)).MessageId.ShouldBe("a02");
    }

    [Fact]
    public async Task With_the_filter_a_group_is_listed_whole_and_counts_the_members_with_an_alternative()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.SuggestionAlternatives.Where(a => a.MessageId.StartsWith("a") && a.MessageId != "a00" && a.MessageId != "a01")
                .ExecuteDeleteAsync(Ct);
        }

        var response = await h.GetAsync($"/api/review/senders/{AnalysisRunHarness.Shop}?hasAlternative=true");
        var group = (await response.Content.ReadFromJsonAsync<ReviewSenderDetailDto>(Ct))!.Groups.ShouldHaveSingleItem();
        (group.Size, group.Members.Count, group.Alternative!.Count).ShouldBe((10, 10, 2));
    }

    [Fact]
    public async Task Accept_closes_the_open_Claude_reviews_of_the_suggestion_and_its_group()
    {
        var ids = await IdsAsync("a00");
        var key = await GroupKeyAsync("a00");
        var now = DateTimeOffset.UtcNow;
        Guid queued = Guid.NewGuid(), reviewed = Guid.NewGuid(), other = Guid.NewGuid();
        await using (var db = postgres.CreateDbContext())
        {
            db.ExternalReviews.AddRange(
                new ExternalReviewRow { Id = queued, TargetType = ExternalReviewTarget.Suggestion, SuggestionId = ids[0], SenderAddress = AnalysisRunHarness.Shop, GroupKey = key, Status = ExternalReviewStatus.Queued, CreatedAt = now },
                new ExternalReviewRow { Id = reviewed, TargetType = ExternalReviewTarget.Group, SenderAddress = AnalysisRunHarness.Shop, GroupKey = key, Status = ExternalReviewStatus.Reviewed, Verdict = ReviewVerdict.Agree, CreatedAt = now },
                new ExternalReviewRow { Id = other, TargetType = ExternalReviewTarget.Group, SenderAddress = AnalysisRunHarness.Billing, GroupKey = await GroupKeyAsync("c00"), Status = ExternalReviewStatus.Queued, CreatedAt = now });
            await db.SaveChangesAsync(Ct);
        }

        (await DecideAsync("accept", new AlternativeDecisionRequest(ids, null))).ShouldBe(new AlternativeDecisionResponse(1, 0, 0));

        await using var check = postgres.CreateDbContext();
        var rows = await check.ExternalReviews.AsNoTracking().ToDictionaryAsync(r => r.Id, Ct);
        rows[queued].Status.ShouldBe(ExternalReviewStatus.Cancelled);
        (rows[reviewed].Status, rows[reviewed].Resolution).ShouldBe((ExternalReviewStatus.Reviewed, ExternalReviewResolution.Dismissed));
        rows[reviewed].ResolvedAt.ShouldNotBeNull();
        rows[other].Status.ShouldBe(ExternalReviewStatus.Queued);
    }

    [Fact]
    public async Task Accept_waiting_on_a_concurrent_discard_counts_the_suggestion_as_skipped()
    {
        var ids = await IdsAsync("a00");
        await using var discard = postgres.CreateDbContext();
        await using var tx = await discard.Database.BeginTransactionAsync(Ct);
        await discard.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM suggestions WHERE id = {ids[0]} FOR UPDATE").ToListAsync(Ct);
        await discard.SuggestionAlternatives.Where(a => a.SuggestionId == ids[0]).ExecuteDeleteAsync(Ct);

        var accept = DecideAsync("accept", new AlternativeDecisionRequest(ids, null));
        await using (var probe = postgres.CreateDbContext())
        {
            // Until the accept waits for the discard's row lock.
            while (!accept.IsCompleted && await probe.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock'")
                .SingleAsync(Ct) == 0)
            {
                await Task.Delay(20, Ct);
            }
        }

        await tx.CommitAsync(Ct);
        (await accept).ShouldBe(new AlternativeDecisionResponse(0, 0, 1));
        await using var check = postgres.CreateDbContext();
        (await check.Suggestions.AsNoTracking().SingleAsync(s => s.Id == ids[0], Ct)).TopicLabel.ShouldBe("Shopping");
    }

    [Fact]
    public async Task Invalid_requests_are_400()
    {
        (await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest(null, null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/review/alternatives/discard", new AlternativeDecisionRequest([], []))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest(null, [new GroupRef(" ", "key")])))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest(null, [new GroupRef(AnalysisRunHarness.Shop, "key", "archived")])))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/claude/reviews", new CreateExternalReviewsRequest(null, [new GroupRef(AnalysisRunHarness.Shop, "key", "applied")], null)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest([.. Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid())], null)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<ReviewSenderDetailDto> DetailAsync(string address) =>
        (await (await h.GetAsync($"/api/review/senders/{address}")).Content.ReadFromJsonAsync<ReviewSenderDetailDto>(Ct)).ShouldNotBeNull();

    private async Task<AlternativeDecisionResponse> DecideAsync(string action, AlternativeDecisionRequest request)
    {
        var response = await h.PostAsync($"/api/review/alternatives/{action}", request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AlternativeDecisionResponse>(Ct)).ShouldNotBeNull();
    }

    private async Task<Guid[]> IdsAsync(params string[] messageIds)
    {
        await using var db = postgres.CreateDbContext();
        return [.. await db.Suggestions.AsNoTracking().Where(s => messageIds.Contains(s.MessageId)).OrderBy(s => s.MessageId).Select(s => s.Id).ToListAsync(Ct)];
    }

    private async Task<string> GroupKeyAsync(string messageId)
    {
        await using var db = postgres.CreateDbContext();
        return (await db.Suggestions.AsNoTracking().SingleAsync(s => s.MessageId == messageId, Ct)).GroupKey.ShouldNotBeNull();
    }
}
