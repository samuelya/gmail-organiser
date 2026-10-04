using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Claude;
using GmailOrganiser.Common;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Mcp;
using GmailOrganiser.Rules.Labels;
using GmailOrganiser.Rules.Review;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Npgsql;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// The M6 Claude review targets (#170): label plan and filter finding items through the review API, and
/// <c>get_filters</c>, <c>get_label_plan</c>, <c>submit_taxonomy_feedback</c> and <c>submit_review</c> on them over MCP.
/// The fake mailbox's three filters are synced; one draft plan and one review with an open and a dismissed finding are
/// seeded.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class McpRulesToolsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly Guid planId = Guid.CreateVersion7();
    private readonly Guid openFinding = Guid.CreateVersion7();
    private readonly Guid dismissedFinding = Guid.CreateVersion7();
    private WebApplicationFactory<Program> host = null!;
    private McpClient client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await postgres.ResetFetchStateAsync();
        await CleanAsync();
        host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true").ConfigureTestServices(services =>
        {
            var retry = new GmailRetryPolicy(Options.Create(new GmailOptions { MaxRetryAttempts = 1 }), TimeProvider.System);
            services.AddSingleton(sp => new FakeGmailClient(sp.GetRequiredService<FakeTokenStore>(), [], retry));
            services.AddScoped<IGmailClient>(sp => sp.GetRequiredService<FakeGmailClient>());
        }));
        (await PostAsync("/api/rules/filters/sync", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var now = DateTimeOffset.UtcNow;
        var plan = new LabelPlanRow { Id = planId, Status = LabelPlanStatus.Draft, LabelCount = 2, CreatedAt = now, UpdatedAt = now };
        plan.WriteItems([new LabelPlanItem(
            Guid.CreateVersion7(), LabelPlanItemKind.Nest, "Label_9", "Synthetic-Receipts", 3, "Synthetic/Receipts", null, null, [],
            "Flat label with a parent prefix.", LabelPlanItemStatus.Proposed)]);
        var review = new FilterReviewRow { Id = Guid.CreateVersion7(), CreatedAt = now, FilterCount = 3, FindingCount = 2 };
        await using var db = postgres.CreateDbContext();
        db.LabelPlans.Add(plan);
        db.FilterReviews.Add(review);
        db.FilterFindings.AddRange(Finding(openFinding, FilterFindingStatus.Open), Finding(dismissedFinding, FilterFindingStatus.Dismissed));
        await db.SaveChangesAsync(Ct);
        client = await McpTestClient.ConnectAsync(host, Ct);

        FilterFindingRow Finding(Guid id, FilterFindingStatus status)
        {
            var row = new FilterFindingRow
            {
                Id = id,
                ReviewId = review.Id,
                Kind = FilterFindingKind.NoRecentMatches,
                FilterIds = ["fake-filter-1"],
                Description = "No recent mail matches this filter.",
                Status = status,
            };
            row.WriteFix(new FilterFix(FilterFixKind.Delete, ["fake-filter-1"]));
            return row;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync();
        await host.DisposeAsync();
        await CleanAsync();
    }

    [Fact]
    public async Task Create_queues_the_plan_and_finding_and_refuses_unknown_closed_or_already_open_targets()
    {
        var created = await CreateAsync(new(null, null, null, planId, [openFinding]));
        created.Created.ShouldBe(2);
        created.Items.Single(i => i.TargetType == "label_plan").LabelPlanId.ShouldBe(planId);
        created.Items.Single(i => i.TargetType == "filter_finding").FindingId.ShouldBe(openFinding);

        (await PostAsync("/api/claude/reviews", new CreateExternalReviewsRequest(null, null, null, planId))).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);
        (await PostAsync("/api/claude/reviews", new CreateExternalReviewsRequest(null, null, null, null, [dismissedFinding]))).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);
        (await PostAsync("/api/claude/reviews", new CreateExternalReviewsRequest(null, null, null, Guid.NewGuid()))).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync("/api/claude/reviews", new CreateExternalReviewsRequest(null, null, null, null, [Guid.NewGuid()]))).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Partial_unique_indexes_allow_one_open_item_per_plan_and_per_finding()
    {
        await CreateAsync(new(null, null, null, planId, [openFinding]));
        await using var db = postgres.CreateDbContext();
        foreach (var (target, index) in new[]
                 {
                     (ExternalReviewTarget.LabelPlan, ExternalReviewRow.OpenLabelPlanIndex),
                     (ExternalReviewTarget.FilterFinding, ExternalReviewRow.OpenFilterFindingIndex),
                 })
        {
            db.ChangeTracker.Clear();
            db.ExternalReviews.Add(new ExternalReviewRow
            {
                Id = Guid.CreateVersion7(),
                TargetType = target,
                Status = ExternalReviewStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
                LabelPlanId = target == ExternalReviewTarget.LabelPlan ? planId : null,
                FilterFindingId = target == ExternalReviewTarget.FilterFinding ? openFinding : null,
            });
            var ex = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
            ex.InnerException.ShouldBeOfType<PostgresException>().ConstraintName.ShouldBe(index);
        }
    }

    [Fact]
    public async Task List_filters_by_label_plan_and_findings_combined_with_status()
    {
        var http = host.CreateClient();
        var items = (await CreateAsync(new(null, null, null, planId, [openFinding]))).Items;
        var cancelled = Guid.CreateVersion7();
        await using (var db = postgres.CreateDbContext())
        {
            db.ExternalReviews.Add(new ExternalReviewRow
            {
                Id = cancelled,
                TargetType = ExternalReviewTarget.FilterFinding,
                Status = ExternalReviewStatus.Cancelled,
                CreatedAt = DateTimeOffset.UtcNow,
                FilterFindingId = dismissedFinding,
            });
            await db.SaveChangesAsync(Ct);
        }

        (await ListAsync($"labelPlanId={planId}")).ShouldBe([items.Single(i => i.LabelPlanId == planId).Id]);
        (await ListAsync($"findingId={openFinding}")).ShouldBe([items.Single(i => i.FindingId == openFinding).Id]);
        (await ListAsync($"findingId={openFinding}&findingId={dismissedFinding}")).Count.ShouldBe(2);
        (await ListAsync($"findingId={openFinding}&findingId={dismissedFinding}&status=cancelled")).ShouldBe([cancelled]);
        (await ListAsync($"labelPlanId={planId}&status=reviewed")).ShouldBeEmpty();
        (await ListAsync($"labelPlanId={planId}&findingId={openFinding}")).ShouldBeEmpty();
        (await ListAsync($"findingId={Guid.NewGuid()}")).ShouldBeEmpty();

        foreach (var query in new[] { "labelPlanId=nope", $"findingId={openFinding}&findingId=nope", "findingId=", "labelPlanId=" })
        {
            var response = await http.GetAsync($"/api/claude/reviews?{query}", Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, query);
        }

        var tooMany = string.Join('&', Enumerable.Range(0, ExternalReviewService.MaxTargets + 1).Select(_ => $"findingId={Guid.NewGuid()}"));
        (await http.GetAsync($"/api/claude/reviews?{tooMany}", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        async Task<List<Guid>> ListAsync(string query) =>
            (await http.GetFromJsonAsync<PagedDto<ExternalReviewDto>>($"/api/claude/reviews?{query}", Ct)).ShouldNotBeNull()
            .Items.Select(i => i.Id).ToList();
    }

    [Fact]
    public async Task List_and_get_review_item_show_plan_and_finding_items()
    {
        var items = (await CreateAsync(new(null, null, null, planId, [openFinding]))).Items;
        var list = McpTestClient.Structured(await McpTestClient.CallAsync(client, "list_pending_reviews", Ct)).GetProperty("items");
        list.EnumerateArray().Select(i => i.GetProperty("targetType").GetString()).Order().ShouldBe(["filter_finding", "label_plan"]);
        list.EnumerateArray().Single(i => i.GetProperty("targetType").GetString() == "label_plan")
            .GetProperty("labelPlanId").GetGuid().ShouldBe(planId);

        var planItem = McpTestClient.Structured(await GetItemAsync(items.Single(i => i.LabelPlanId == planId).Id));
        planItem.GetProperty("plan").GetProperty("id").GetGuid().ShouldBe(planId);
        planItem.GetProperty("plan").GetProperty("items")[0].GetProperty("rationale").GetString().ShouldBe("Flat label with a parent prefix.");

        var findingItem = McpTestClient.Structured(await GetItemAsync(items.Single(i => i.FindingId == openFinding).Id));
        findingItem.GetProperty("finding").GetProperty("kind").GetString().ShouldBe("no_recent_matches");
        findingItem.GetProperty("finding").GetProperty("fix").GetProperty("kind").GetString().ShouldBe("delete");
        findingItem.GetProperty("filters").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ShouldBe(["fake-filter-1"]);
    }

    [Fact]
    public async Task Get_filters_returns_the_snapshot_with_label_names_and_the_latest_review()
    {
        var json = McpTestClient.Structured(await McpTestClient.CallAsync(client, "get_filters", Ct));

        json.GetProperty("activeCount").GetInt32().ShouldBe(3);
        json.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        var news = json.GetProperty("filters").EnumerateArray().Single(f => f.GetProperty("id").GetString() == "fake-filter-1");
        news.GetProperty("criteriaSummary").GetString().ShouldBe("from:news@example.com");
        news.GetProperty("action").GetProperty("addLabels").EnumerateArray().Select(l => l.GetString())
            .ShouldBe([FakeLabelStore.SeedUserLabelNames[0]]);
        json.GetProperty("review").GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("id").GetGuid())
            .ShouldBe([openFinding, dismissedFinding], ignoreOrder: true);
    }

    [Fact]
    public async Task Get_label_plan_returns_the_draft_and_errors_for_an_unknown_plan()
    {
        var json = McpTestClient.Structured(await McpTestClient.CallAsync(client, "get_label_plan", Ct));
        json.GetProperty("plan").GetProperty("id").GetGuid().ShouldBe(planId);
        json.GetProperty("plan").GetProperty("status").GetString().ShouldBe("draft");

        McpTestClient.ErrorText(await McpTestClient.CallAsync(client, "get_label_plan", Ct, new Dictionary<string, object?>
        {
            ["plan_id"] = Guid.NewGuid().ToString(),
        })).ShouldContain("No label plan");
        McpTestClient.ErrorText(await McpTestClient.CallAsync(client, "get_label_plan", Ct, new Dictionary<string, object?> { ["plan_id"] = "x" }))
            .ShouldContain("not a plan id");
    }

    [Fact]
    public async Task Taxonomy_feedback_stores_an_alternative_that_accept_records_without_touching_the_plan()
    {
        var item = (await CreateAsync(new(null, null, null, planId))).Items.Single().Id;

        var result = McpTestClient.Structured(await FeedbackAsync(new()
        {
            ["plan_id"] = planId.ToString(),
            ["comments"] = "Group receipts under one parent.",
            ["alternative_structure"] = new[] { "Synthetic/Receipts", " Synthetic/Travel " },
        }));
        result.GetProperty("ok").GetBoolean().ShouldBeTrue(result.GetRawText());
        result.GetProperty("status").GetString().ShouldBe("reviewed");

        var dto = await GetAsync(item);
        dto.Verdict.ShouldBe("alternative");
        dto.Reasoning.ShouldBe("Group receipts under one parent.");
        dto.AlternativeStructure.ShouldBe(["Synthetic/Receipts", "Synthetic/Travel"]);

        (await PostAsync($"/api/claude/reviews/{item}/accept", new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(item)).Resolution.ShouldBe("accepted_claude");
        await using var db = postgres.CreateDbContext();
        (await db.LabelPlans.SingleAsync(p => p.Id == planId, Ct)).Status.ShouldBe(LabelPlanStatus.Draft);
    }

    [Fact]
    public async Task Taxonomy_feedback_with_an_empty_structure_is_an_agree()
    {
        var item = (await CreateAsync(new(null, null, null, planId))).Items.Single().Id;

        var result = McpTestClient.Structured(await FeedbackAsync(new()
        {
            ["plan_id"] = planId.ToString(),
            ["comments"] = "The plan reads well.",
            ["alternative_structure"] = Array.Empty<string>(),
        }));
        result.GetProperty("ok").GetBoolean().ShouldBeTrue(result.GetRawText());

        var dto = await GetAsync(item);
        dto.Verdict.ShouldBe("agree");
        dto.Reasoning.ShouldBe("The plan reads well.");
        dto.AlternativeStructure.ShouldBeNull();
    }

    [Fact]
    public async Task Taxonomy_feedback_refuses_invalid_paths_a_plan_without_an_item_and_a_discarded_plan()
    {
        McpTestClient.ErrorText(await FeedbackAsync(new() { ["plan_id"] = planId.ToString(), ["comments"] = "Fine." }))
            .ShouldContain("no open review item");

        var item = (await CreateAsync(new(null, null, null, planId))).Items.Single().Id;
        var invalid = McpTestClient.Structured(await FeedbackAsync(new()
        {
            ["plan_id"] = planId.ToString(),
            ["comments"] = "Rename.",
            ["alternative_structure"] = new[] { "INBOX" },
        }));
        invalid.GetProperty("ok").GetBoolean().ShouldBeFalse();
        invalid.GetProperty("reason").GetString().ShouldNotBeNull().ShouldContain("alternative_structure[0]");

        McpTestClient.Structured(await SubmitReviewAsync(item, "agree")).GetProperty("reason").GetString().ShouldNotBeNull()
            .ShouldContain("submit_taxonomy_feedback");

        (await PostAsync($"/api/rules/labels/plans/{planId}/discard", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var decided = McpTestClient.Structured(await FeedbackAsync(new() { ["plan_id"] = planId.ToString(), ["comments"] = "Fine." }));
        decided.GetProperty("ok").GetBoolean().ShouldBeFalse();
        decided.GetProperty("status").GetString().ShouldBe("cancelled");
    }

    [Fact]
    public async Task Submit_review_on_a_finding_takes_filter_criteria_for_an_alternative()
    {
        var item = (await CreateAsync(new(null, null, null, null, [openFinding]))).Items.Single().Id;

        McpTestClient.Structured(await SubmitReviewAsync(item, "alternative")).GetProperty("reason").GetString().ShouldNotBeNull()
            .ShouldContain("filter_criteria");
        McpTestClient.Structured(await SubmitReviewAsync(item, "alternative", ("topic_label", "Synthetic/Other"), ("filter_criteria", "from:a")))
            .GetProperty("ok").GetBoolean().ShouldBeFalse();

        var ok = McpTestClient.Structured(await SubmitReviewAsync(item, "alternative", ("filter_criteria", "from:news@example.com older_than:1y")));
        ok.GetProperty("ok").GetBoolean().ShouldBeTrue(ok.GetRawText());
        await using (var db = postgres.CreateDbContext())
        {
            var row = await db.ExternalReviews.AsNoTracking().SingleAsync(r => r.Id == item, Ct);
            row.Verdict.ShouldBe(ReviewVerdict.Alternative);
            row.VerdictFilterCriteria.ShouldBe("\"from:news@example.com older_than:1y\"");
        }

        var dto = await GetAsync(item);
        dto.Verdict.ShouldBe("alternative");
        dto.VerdictFilterCriteria.ShouldBe("from:news@example.com older_than:1y");

        (await PostAsync($"/api/claude/reviews/{item}/accept", new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_finding_decided_after_sending_cancels_its_item_on_submit_and_is_not_taken_by_a_run()
    {
        var item = (await CreateAsync(new(null, null, null, null, [openFinding]))).Items.Single().Id;
        await using (var db = postgres.CreateDbContext())
        {
            await db.FilterFindings.Where(f => f.Id == openFinding)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, FilterFindingStatus.Dismissed), Ct);
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ExternalReviewService>().MarkRunningAsync([item], Guid.NewGuid(), Ct)).ShouldBe(0);
        }

        (await GetAsync(item)).Status.ShouldBe("cancelled");
        (await PostAsync($"/api/claude/reviews/{item}/retry", new { })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Review_label_plan_prompt_is_served_and_the_headless_run_may_use_the_rules_tools()
    {
        var result = await client.GetPromptAsync(
            ReviewPrompts.ReviewLabelPlanName, new Dictionary<string, object?> { ["plan_id"] = planId.ToString() }, cancellationToken: Ct);
        var text = result.Messages.ShouldHaveSingleItem().Content.ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain($"`get_label_plan` with `plan_id` {planId}");
        text.ShouldContain("`submit_taxonomy_feedback`");
        text.ShouldNotContain("{{");

        foreach (var tool in new[] { "get_filters", "get_label_plan", "submit_taxonomy_feedback" })
        {
            ClaudeReviewJob.AllowedTools.ShouldContain($"mcp__{McpExtensions.ServerName}__{tool}");
        }
    }

    private Task<CallToolResult> GetItemAsync(Guid id) =>
        McpTestClient.CallAsync(client, "get_review_item", Ct, new Dictionary<string, object?> { ["id"] = id.ToString() });

    private Task<CallToolResult> FeedbackAsync(Dictionary<string, object?> arguments) =>
        McpTestClient.CallAsync(client, "submit_taxonomy_feedback", Ct, arguments);

    private Task<CallToolResult> SubmitReviewAsync(Guid id, string verdict, params (string Name, string Value)[] extra)
    {
        var arguments = new Dictionary<string, object?> { ["id"] = id.ToString(), ["verdict"] = verdict, ["reasoning"] = "Synthetic reasoning." };
        foreach (var (name, value) in extra)
        {
            arguments[name] = value;
        }

        return McpTestClient.CallAsync(client, "submit_review", Ct, arguments);
    }

    private async Task<CreateExternalReviewsResponse> CreateAsync(CreateExternalReviewsRequest request)
    {
        var response = await PostAsync("/api/claude/reviews", request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CreateExternalReviewsResponse>(JsonSerializerOptions.Web, Ct)).ShouldNotBeNull();
    }

    private async Task<ExternalReviewDto> GetAsync(Guid id) =>
        (await host.CreateClient().GetFromJsonAsync<ExternalReviewDto>($"/api/claude/reviews/{id}", Ct)).ShouldNotBeNull();

    private Task<HttpResponseMessage> PostAsync(string path, object? body)
    {
        var http = host.CreateClient();
        http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return http.PostAsJsonAsync(path, body, Ct);
    }

    private async Task CleanAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.ExternalReviews.ExecuteDeleteAsync(Ct);
        await db.LabelPlans.ExecuteDeleteAsync(Ct);
        await db.FilterReviews.ExecuteDeleteAsync(Ct);
        await db.Filters.ExecuteDeleteAsync(Ct);
    }
}
