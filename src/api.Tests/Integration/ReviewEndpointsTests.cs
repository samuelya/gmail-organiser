using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// Review over a finished inbox run of the harness mailbox: shop 10 (3 model, 7 derived at 0.8), news 6 (3 + 3) and
/// billing 4 (3 + 1); model answers have confidence 0.9.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReviewEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Senders_list_counts_per_status_with_the_most_in_that_status_first()
    {
        await PostOkAsync($"/api/review/suggestions/{await IdAsync("a00")}/approve");

        var pending = await ListAsync("/api/review/senders");
        pending.Total.ShouldBe(3);
        pending.Items.Select(s => (s.Address, s.Pending, s.Approved)).ShouldBe(
            [(AnalysisRunHarness.Shop, 9, 1), (AnalysisRunHarness.News, 6, 0), (AnalysisRunHarness.Billing, 4, 0)]);
        pending.Items[0].TotalMessages.ShouldBe(10);

        (await ListAsync("/api/review/senders?status=approved")).Items.ShouldHaveSingleItem().Address.ShouldBe(AnalysisRunHarness.Shop);
        (await ListAsync("/api/review/senders?status=rejected")).Total.ShouldBe(0);
        (await ListAsync("/api/review/senders?search=NEWS")).Items.ShouldHaveSingleItem().Address.ShouldBe(AnalysisRunHarness.News);
        (await ListAsync("/api/review/senders?search=%25")).Total.ShouldBe(0);
        var paged = await ListAsync("/api/review/senders?page=2&pageSize=2");
        (paged.Total, paged.Items.ShouldHaveSingleItem().Address).ShouldBe((3, AnalysisRunHarness.Billing));
        (await h.GetAsync("/api/review/senders?status=applied")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.GetAsync("/api/review/senders?pageSize=0")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sender_detail_groups_members_with_aggregates_and_protection()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => m.Id == "a03").ExecuteUpdateAsync(s => s.SetProperty(m => m.HasAttachment, true), Ct);
        }

        var detail = await DetailAsync(AnalysisRunHarness.Shop.ToUpperInvariant());

        detail.Sender.Pending.ShouldBe(10);
        var group = detail.Groups.ShouldHaveSingleItem();
        (group.Size, group.LlmCount, group.DerivedCount, group.MemoryCount).ShouldBe((10, 3, 7, 0));
        (group.TopicLabel, group.Mixed, group.Truncated, group.Display).ShouldBe(("Shopping", false, false, "Weekly offer 1"));
        group.ConfidenceMin.ShouldBe(0.8, 1e-9);
        group.ConfidenceMax.ShouldBe(0.9, 1e-9);
        group.Reason.ShouldBe("Synthetic reason");
        group.GroupKey.ShouldNotBeNull().ShouldStartWith("from:shop@example.com|");
        group.Members.Select(m => m.MessageId).ShouldBe(Enumerable.Range(0, 10).Select(i => $"a{i:D2}"));
        group.Members.Single(m => m.Protected).MessageId.ShouldBe("a03");
        group.Members.ShouldAllBe(m => m.Status == "pending" && (m.Source == "llm" || m.Source == "derived"));

        (await h.GetAsync("/api/review/senders/nobody@example.com")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await h.GetAsync($"/api/review/senders/{AnalysisRunHarness.Shop}?status=nope")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailAsync(AnalysisRunHarness.Shop, "approved")).Groups.ShouldBeEmpty();
    }

    [Fact]
    public async Task Transitions_follow_the_table_and_write_one_decision_per_change()
    {
        var id = await IdAsync("a05");

        (await PostOkAsync($"/api/review/suggestions/{id}/approve")).Status.ShouldBe("approved");
        (await PostOkAsync($"/api/review/suggestions/{id}/approve")).Status.ShouldBe("approved"); // no-op
        (await PostOkAsync($"/api/review/suggestions/{id}/reject")).Status.ShouldBe("rejected");
        (await PostOkAsync($"/api/review/suggestions/{id}/approve")).Status.ShouldBe("approved");

        await using var db = postgres.CreateDbContext();
        (await db.Decisions.AsNoTracking().OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).Select(d => d.Outcome).ToListAsync(Ct))
            .ShouldBe([DecisionOutcome.Approved, DecisionOutcome.Rejected, DecisionOutcome.Approved]);
        (await db.Messages.AsNoTracking().SingleAsync(m => m.Id == "a05", Ct)).AnalysisStatus.ShouldBe(AnalysisStatus.Approved);
        var row = await db.Suggestions.AsNoTracking().SingleAsync(s => s.Id == id, Ct);
        row.DecidedAt.ShouldNotBeNull();

        await AnalysisRunHarness.DecideAsync(db, "a05", SuggestionStatus.Applied);
        (await h.PostAsync($"/api/review/suggestions/{id}/reject", new { })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await h.PutAsync($"/api/review/suggestions/{id}", new EditSuggestionRequest("Other", false, false))).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);
        (await h.PostAsync($"/api/review/suggestions/{Guid.NewGuid()}/approve", new { })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await db.Decisions.CountAsync(Ct)).ShouldBe(3);
        (await db.Messages.AsNoTracking().SingleAsync(m => m.Id == "a05", Ct)).AnalysisStatus.ShouldBe(AnalysisStatus.Applied);
    }

    [Fact]
    public async Task Decision_rows_copy_sender_list_template_label_and_flags()
    {
        await using (var db = postgres.CreateDbContext())
        {
            // A list id with '|' in it: the template is not parsed out of the group key.
            await db.Messages.Where(m => m.Id == "c01").ExecuteUpdateAsync(s => s.SetProperty(m => m.ListId, "<billing|x.example.com>"), Ct);
            var message = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == "c01", Ct);
            await db.Suggestions.Where(s => s.MessageId == "c01").ExecuteUpdateAsync(s => s.SetProperty(x => x.GroupKey, GroupKey.For(message)), Ct);
        }

        await PostOkAsync($"/api/review/suggestions/{await IdAsync("a02")}/reject");
        await PostOkAsync($"/api/review/suggestions/{await IdAsync("c01")}/approve");

        await using var check = postgres.CreateDbContext();
        var rows = await check.Decisions.AsNoTracking().ToDictionaryAsync(d => d.MessageId!, Ct);
        var shop = rows["a02"];
        (shop.SenderAddress, shop.Outcome, shop.TopicLabel, shop.Edited, shop.ListId).ShouldBe(
            (AnalysisRunHarness.Shop, DecisionOutcome.Rejected, "Shopping", false, null));
        shop.SubjectTemplate.ShouldNotBeNullOrWhiteSpace();
        shop.SubjectTemplate.ShouldNotContain("from:");
        shop.SubjectTemplate.ShouldNotContain("|");
        (rows["c01"].ListId, rows["c01"].Outcome, rows["c01"].Embedding).ShouldBe(("<billing|x.example.com>", DecisionOutcome.Approved, null));
        rows["c01"].SubjectTemplate.ShouldBe(SubjectNormaliser.Template("Invoice 2"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("INBOX")]
    [InlineData("a//b")]
    [InlineData("Bills/ Water")]
    [InlineData("a/b/c/d/e/f")]
    public async Task Edit_rejects_an_invalid_label_path(string label)
    {
        var response = await h.PutAsync($"/api/review/suggestions/{await IdAsync("a01")}", new EditSuggestionRequest(label, false, false));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("topicLabel");
    }

    [Fact]
    public async Task Edit_approves_the_changed_outcome_and_a_group_with_an_edited_member_is_mixed()
    {
        var group = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.Single();
        (await PostAsync<GroupDecisionResponse>("/api/review/groups/approve", Approve(AnalysisRunHarness.Shop, group)))
            .Changed.ShouldBe(10);

        var response = await h.PutAsync($"/api/review/suggestions/{await IdAsync("a04")}", new EditSuggestionRequest(" Deals/Weekly ", true, false));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var edited = (await response.Content.ReadFromJsonAsync<SuggestionDto>(Ct)).ShouldNotBeNull();
        (edited.TopicLabel, edited.NeedsAction, edited.Edited, edited.Status).ShouldBe(("Deals/Weekly", true, true, "approved"));

        var approved = (await DetailAsync(AnalysisRunHarness.Shop, "approved")).Groups.Single();
        (approved.Mixed, approved.TopicLabel, approved.Size).ShouldBe((true, "Shopping", 10));
        await using var db = postgres.CreateDbContext();
        var decision = await db.Decisions.AsNoTracking().Where(d => d.MessageId == "a04").OrderByDescending(d => d.CreatedAt).FirstAsync(Ct);
        (decision.Edited, decision.TopicLabel, decision.NeedsAction).ShouldBe((true, "Deals/Weekly", true));
    }

    [Fact]
    public async Task Group_decisions_act_on_pending_members_only()
    {
        var group = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.Single();
        var derived = group.Members.First(m => m.Source == "derived");
        await PostOkAsync($"/api/review/suggestions/{derived.Id}/reject");

        // Rejecting a derived member does not reject the group.
        var rest = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.Single();
        (rest.Size, rest.GroupKey).ShouldBe((9, group.GroupKey));

        (await PostAsync<GroupDecisionResponse>("/api/review/groups/reject", new GroupDecisionRequest(AnalysisRunHarness.Shop, group.GroupKey)))
            .Changed.ShouldBe(9);
        (await PostAsync<GroupDecisionResponse>("/api/review/groups/approve", Approve(AnalysisRunHarness.Shop, group)))
            .Changed.ShouldBe(0);
        (await PostAsync<GroupDecisionResponse>("/api/review/groups/approve", Approve(AnalysisRunHarness.News, group)))
            .Changed.ShouldBe(0);
        (await h.PostAsync("/api/review/groups/approve", Approve(AnalysisRunHarness.Shop, group) with { GroupKey = null })).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/review/groups/approve", Approve(" ", group))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);

        await using var db = postgres.CreateDbContext();
        (await db.Decisions.CountAsync(d => d.Outcome == DecisionOutcome.Rejected, Ct)).ShouldBe(10);
        (await db.Messages.AsNoTracking().Where(m => m.Id.StartsWith("a")).Select(m => m.AnalysisStatus).ToListAsync(Ct))
            .ShouldAllBe(s => s == AnalysisStatus.Rejected);
    }

    [Fact]
    public async Task Bulk_approve_takes_model_answers_above_the_threshold_and_skips_protected_deletions()
    {
        var protectedId = await IdAsync("b00");
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.Id == protectedId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ToBeDeleted, true), Ct);
            await db.Messages.Where(m => m.Id == "b00").ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, new[] { "INBOX", "STARRED" }), Ct);
        }

        (await BulkAsync(new BulkApproveRequest(0.95))).ShouldBe((0, 0));
        (await BulkAsync(new BulkApproveRequest(null, SenderAddress: "SHOP@example.com"))).ShouldBe((3, 0));

        // Default threshold 0.80: the other model answers, not the derived ones (0.80) and not the protected deletion.
        var llm = await PostAsync<BulkApproveResponse>("/api/review/bulk-approve", new BulkApproveRequest(null));
        (llm.Approved, llm.SkippedProtected, llm.SkippedIds.ShouldHaveSingleItem()).ShouldBe((5, 1, protectedId));

        (await BulkAsync(new BulkApproveRequest(0.75))).ShouldBe((0, 1));
        (await BulkAsync(new BulkApproveRequest(0.75, IncludeDerived: true))).ShouldBe((11, 1));

        await using var check = postgres.CreateDbContext();
        (await check.Suggestions.AsNoTracking().SingleAsync(s => s.Id == protectedId, Ct)).Status.ShouldBe(SuggestionStatus.Pending);
        (await check.Suggestions.CountAsync(s => s.Status == SuggestionStatus.Approved, Ct)).ShouldBe(19);
        (await check.Decisions.CountAsync(Ct)).ShouldBe(19);
        (await h.PostAsync("/api/review/bulk-approve", new BulkApproveRequest(0.3))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/review/bulk-approve", new BulkApproveRequest(1.5))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Saved_protection_rules_apply_a_starred_deletion_is_approved_once_the_starred_rule_is_off()
    {
        var starredId = await IdAsync("b00");
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.Id == starredId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ToBeDeleted, true), Ct);
            await db.Messages.Where(m => m.Id == "b00").ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, new[] { "INBOX", "STARRED" }), Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
                .UpdateAsync(x => x with { Protection = new ProtectionSettings(Starred: false) }, Ct);
        }

        // Default threshold 0.80: all nine model answers, the starred deletion among them.
        (await BulkAsync(new BulkApproveRequest(null))).ShouldBe((9, 0));
        (await PostOkAsync($"/api/review/suggestions/{starredId}/approve")).Protected.ShouldBeFalse();
    }

    [Fact]
    public async Task Bulk_approve_skips_a_deletion_from_an_allowlisted_domain()
    {
        var deletionId = await IdAsync("b00");
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.Id == deletionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ToBeDeleted, true), Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
                .UpdateAsync(x => x with { Protection = x.Protection with { AllowlistedDomains = ["example.com"] } }, Ct);
        }

        (await BulkAsync(new BulkApproveRequest(null))).ShouldBe((8, 1));
        (await PostOkAsync($"/api/review/suggestions/{deletionId}/approve")).Protected.ShouldBeTrue();
    }

    [Fact]
    public async Task Analyse_individually_resets_the_members_and_queues_an_ungrouped_messages_run()
    {
        var pending = await IdAsync("a05");
        var approved = await IdAsync("a06");
        await PostOkAsync($"/api/review/suggestions/{approved}/approve");

        var response = await h.PostAsync("/api/review/analyse-individually", new AnalyseIndividuallyRequest([pending, approved]));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var run = (await response.Content.ReadFromJsonAsync<AnalysisRunDto>(Ct)).ShouldNotBeNull();
        (run.Scope, run.GroupingMode, run.RequestedCount).ShouldBe(("messages", "off", 1));
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Suggestions.AnyAsync(s => s.Id == pending, Ct)).ShouldBeFalse();
            (await db.Messages.AsNoTracking().SingleAsync(m => m.Id == "a05", Ct)).AnalysisStatus.ShouldBe(AnalysisStatus.NotAnalysed);
            (await db.Suggestions.AsNoTracking().SingleAsync(s => s.Id == approved, Ct)).Status.ShouldBe(SuggestionStatus.Approved);
        }

        await h.RunNextAsync();
        await using var check = postgres.CreateDbContext();
        (await check.Suggestions.AsNoTracking().SingleAsync(s => s.MessageId == "a05", Ct)).Source.ShouldBe(SuggestionSource.Llm);
    }

    [Fact]
    public async Task Analyse_individually_refuses_decided_unknown_and_invalid_selections()
    {
        var approved = await IdAsync("a06");
        await PostOkAsync($"/api/review/suggestions/{approved}/approve");

        (await h.PostAsync("/api/review/analyse-individually", new AnalyseIndividuallyRequest([approved]))).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);
        (await h.PostAsync("/api/review/analyse-individually", new AnalyseIndividuallyRequest([Guid.NewGuid()]))).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await h.PostAsync("/api/review/analyse-individually", new AnalyseIndividuallyRequest([]))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/review/analyse-individually", new AnalyseIndividuallyRequest([.. Enumerable.Range(0, 501).Select(_ => Guid.NewGuid())])))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await h.SetChatModelAsync(null);
        var pending = await IdAsync("a05");
        (await h.PostAsync("/api/review/analyse-individually", new AnalyseIndividuallyRequest([pending]))).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);
        await using var db = postgres.CreateDbContext();
        (await db.Suggestions.AnyAsync(s => s.Id == pending, Ct)).ShouldBeTrue();
        (await db.AnalysisRuns.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Group_approve_takes_only_the_cards_outcome_and_never_a_protected_deletion()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Shop)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ToBeDeleted, true), Ct);
            await db.Suggestions.Where(s => s.MessageId == "a01").ExecuteUpdateAsync(s => s.SetProperty(x => x.TopicLabel, "Other"), Ct);
            await db.Messages.Where(m => m.Id == "a03").ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, new[] { "INBOX", "STARRED" }), Ct);
        }

        var group = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.Single();
        (group.TopicLabel, group.ToBeDeleted, group.Mixed).ShouldBe(("Shopping", true, true));
        (await h.PostAsync("/api/review/groups/approve", new GroupDecisionRequest(AnalysisRunHarness.Shop, group.GroupKey))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);

        var result = await PostAsync<GroupDecisionResponse>("/api/review/groups/approve", Approve(AnalysisRunHarness.Shop, group));

        result.Changed.ShouldBe(8);
        result.Skipped.ShouldBe([await IdAsync("a01"), await IdAsync("a03")], ignoreOrder: true);
        (await PostOkAsync($"/api/review/suggestions/{await IdAsync("a03")}/approve")).Status.ShouldBe("approved");
        await using var check = postgres.CreateDbContext();
        (await check.Suggestions.AsNoTracking().SingleAsync(s => s.MessageId == "a01", Ct)).Status.ShouldBe(SuggestionStatus.Pending);
    }

    [Fact]
    public async Task Edit_needs_both_flags_and_recomputes_is_new_label()
    {
        var id = await IdAsync("a01");
        var missing = await h.PutAsync($"/api/review/suggestions/{id}", new { topicLabel = "Shopping" });
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await missing.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("needsAction");
        body.ShouldContain("toBeDeleted");

        (await EditAsync(id, FakeLabelStore.SeedUserLabelNames[0].ToUpperInvariant())).IsNewLabel.ShouldBeFalse();
        (await EditAsync(id, "Synthetic/Brand new")).IsNewLabel.ShouldBeTrue();

        // Without Gmail the edit still works; a changed label counts as new.
        await h.Services.GetRequiredService<FakeTokenStore>().DeleteAsync(Ct);
        (await EditAsync(id, "Synthetic/Offline")).IsNewLabel.ShouldBeTrue();
    }

    [Fact]
    public async Task Sender_detail_pages_its_groups()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.MessageId == "b04" || s.MessageId == "b05")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.GroupKey, (string?)null), Ct);
        }

        var first = await DetailAsync(AnalysisRunHarness.News, query: "page=1&pageSize=2");
        (first.TotalGroups, first.Page, first.PageSize).ShouldBe((3, 1, 2));
        first.Groups.Select(g => g.Size).ShouldBe([4, 1]);
        first.Groups[0].Members.Count.ShouldBe(4);
        var second = await DetailAsync(AnalysisRunHarness.News, query: "page=2&pageSize=2");
        second.Groups.ShouldHaveSingleItem().GroupKey.ShouldBeNull();
        first.Groups[1].Members.Single().MessageId.ShouldNotBe(second.Groups[0].Members.Single().MessageId);

        (await h.GetAsync($"/api/review/senders/{AnalysisRunHarness.News}?pageSize={ReviewQuery.MaxGroupPageSize + 1}")).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await h.GetAsync($"/api/review/senders/{AnalysisRunHarness.News}?page=0")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_member_approved_while_a_run_stores_it_is_skipped_and_the_run_completes()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("messages", null, ["a05"], null, "off"));
        await using var reviewer = postgres.CreateDbContext();
        await using var tx = await reviewer.Database.BeginTransactionAsync(Ct);
        Task? commit = null;
        h.Chat.Respond = async (ids, _, _, _) =>
        {
            // The approve is uncommitted while the run checks; it commits once the run waits for the row lock.
            await reviewer.Database.ExecuteSqlAsync($"UPDATE suggestions SET status = 'approved' WHERE message_id = 'a05'", Ct);
            commit = CommitWhenBlockedAsync();
            return AnalysisRunHarness.Agree(ids);
        };

        await h.RunNextAsync();
        await commit.ShouldNotBeNull();

        (await h.GetRunAsync(run.Id)).Status.ShouldBe("completed");
        await using var check = postgres.CreateDbContext();
        var stored = await check.AnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id, Ct);
        (stored.MessagesCovered, stored.MessagesLlm, stored.SkippedMessages).ShouldBe((0, 0, 1));
        (await check.Suggestions.AsNoTracking().SingleAsync(s => s.MessageId == "a05", Ct)).Status.ShouldBe(SuggestionStatus.Approved);

        async Task CommitWhenBlockedAsync()
        {
            await using var probe = postgres.CreateDbContext();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (await probe.Database.SqlQuery<int>($"""
                    SELECT count(*)::int AS "Value" FROM pg_stat_activity
                    WHERE datname = current_database() AND wait_event_type = 'Lock'
                    """).SingleAsync(timeout.Token) == 0)
            {
                await Task.Delay(20, timeout.Token);
            }

            await tx.CommitAsync(Ct);
        }
    }

    private static GroupDecisionRequest Approve(string sender, ReviewGroupDto group) =>
        new(sender, group.GroupKey, group.TopicLabel, group.NeedsAction, group.ToBeDeleted);

    private async Task<SuggestionDto> EditAsync(Guid id, string label)
    {
        var response = await h.PutAsync($"/api/review/suggestions/{id}", new EditSuggestionRequest(label, false, false));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SuggestionDto>(Ct)).ShouldNotBeNull();
    }

    private async Task<(int Approved, int SkippedProtected)> BulkAsync(BulkApproveRequest request)
    {
        var response = await PostAsync<BulkApproveResponse>("/api/review/bulk-approve", request);
        return (response.Approved, response.SkippedProtected);
    }

    private async Task<Guid> IdAsync(string messageId)
    {
        await using var db = postgres.CreateDbContext();
        return await db.Suggestions.Where(s => s.MessageId == messageId).Select(s => s.Id).SingleAsync(Ct);
    }

    private async Task<SuggestionDto> PostOkAsync(string path) => await PostAsync<SuggestionDto>(path, new { });

    private async Task<T> PostAsync<T>(string path, object body)
        where T : class
    {
        var response = await h.PostAsync(path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct)).ShouldNotBeNull();
    }

    private async Task<PagedDto<ReviewSenderDto>> ListAsync(string path)
    {
        var response = await h.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PagedDto<ReviewSenderDto>>(Ct)).ShouldNotBeNull();
    }

    private async Task<ReviewSenderDetailDto> DetailAsync(string address, string? status = null, string? query = null)
    {
        var response = await h.GetAsync($"/api/review/senders/{Uri.EscapeDataString(address)}?status={status ?? "pending"}&{query}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ReviewSenderDetailDto>(Ct)).ShouldNotBeNull();
    }
}
