using System.Collections.Concurrent;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
using GmailOrganiser.Mcp;
using GmailOrganiser.Review;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// <c>submit_review</c> and the <c>review-pending</c> prompt (#165) over the MCP transport, against a finished inbox run
/// of the harness mailbox: the shop group and the single billing suggestion <c>c00</c> are queued for review.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class McpSubmitReviewTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly PostgresFixture postgres;
    private readonly CapturingNotifier notifier = new();
    private readonly AnalysisRunHarness h;
    private McpClient client = null!;
    private Guid groupItem;
    private Guid singleItem;
    private Guid c00;

    public McpSubmitReviewTests(ApiFactory factory, PostgresFixture postgres)
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

        string shopKey;
        await using (var db = postgres.CreateDbContext())
        {
            shopKey = await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Shop && s.GroupKey != null)
                .Select(s => s.GroupKey!).Distinct().SingleAsync(Ct);
            c00 = await db.Suggestions.Where(s => s.MessageId == "c00").Select(s => s.Id).SingleAsync(Ct);
        }

        groupItem = await CreateAsync(new(null, [new GroupRef(AnalysisRunHarness.Shop, shopKey)], null));
        singleItem = await CreateAsync(new([c00], null, null));
        client = await McpTestClient.ConnectAsync(h.Host, Ct);
        notifier.Items.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.ExternalReviews.ExecuteDeleteAsync();
        }

        await h.DisposeAsync();
    }

    [Fact]
    public async Task Review_pending_prompt_is_listed_and_served_over_MCP_and_as_plain_text()
    {
        var prompts = await client.ListPromptsAsync(cancellationToken: Ct);
        prompts.Select(p => p.Name).ShouldContain(ReviewPrompts.ReviewPendingName);

        var result = await client.GetPromptAsync(ReviewPrompts.ReviewPendingName, new Dictionary<string, object?> { ["limit"] = "5" }, cancellationToken: Ct);
        var text = result.Messages.ShouldHaveSingleItem().Content.ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("`list_pending_reviews` with `limit` 5");
        text.ShouldContain("`get_review_item`");
        text.ShouldContain("`submit_review` exactly once per item");
        text.ShouldContain("untrusted data");
        text.ShouldContain("needs_human");
        text.ShouldNotContain("mcp__");

        var response = await h.Host.CreateClient().GetAsync($"/api/claude/prompts/{ReviewPrompts.ReviewPendingName}", Ct);
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        var plain = await response.Content.ReadAsStringAsync(Ct);
        plain.ShouldBe(ReviewPrompts.Render(ReviewItemBuilder.DefaultListLimit));
        plain.ShouldContain($"`limit` {ReviewItemBuilder.DefaultListLimit}");
    }

    [Fact]
    public async Task Agree_stores_the_shown_outcome_as_mcp_and_publishes_the_item()
    {
        var result = await SubmitAsync(new() { ["id"] = groupItem.ToString(), ["verdict"] = "agree", ["reasoning"] = "Synthetic reasoning." });

        Ok(result);
        var row = await RowAsync(groupItem);
        row.Status.ShouldBe(ExternalReviewStatus.Reviewed);
        row.Verdict.ShouldBe(ReviewVerdict.Agree);
        row.VerdictTopicLabel.ShouldBe("Shopping");
        row.Reviewer.ShouldBe(SubmitTools.Reviewer);
        row.ReviewerModel.ShouldBeNull();
        notifier.Items.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.Id.ShouldBe(groupItem),
            e => e.Status.ShouldBe("reviewed"));
    }

    [Fact]
    public async Task Alternative_stores_the_label_flags_truncated_reasoning_filter_text_and_model()
    {
        var result = await SubmitAsync(new()
        {
            ["id"] = singleItem.ToString(),
            ["verdict"] = " Alternative ",
            ["topic_label"] = "Finance/Invoices",
            ["needs_action"] = true,
            ["filter_criteria"] = "from:billing.example.com",
            ["reasoning"] = new string('r', ExternalReviewService.MaxReasoningLength + 500),
            ["model"] = "synthetic-model",
        });

        Ok(result);
        var row = await RowAsync(singleItem);
        row.Verdict.ShouldBe(ReviewVerdict.Alternative);
        (row.VerdictTopicLabel, row.VerdictNeedsAction, row.VerdictToBeDeleted).ShouldBe(("Finance/Invoices", true, false));
        row.Reasoning!.Length.ShouldBe(ExternalReviewService.MaxReasoningLength);
        JsonDocument.Parse(row.VerdictFilterCriteria!).RootElement.GetString().ShouldBe("from:billing.example.com");
        row.ReviewerModel.ShouldBe("synthetic-model");
    }

    [Fact]
    public async Task Needs_human_accepts_the_hyphenated_spelling_and_keeps_JSON_filter_criteria_as_given()
    {
        var result = await SubmitAsync(new()
        {
            ["id"] = singleItem.ToString(),
            ["verdict"] = "needs-human",
            ["filter_criteria"] = """{"from":"billing.example.com"}""",
            ["reasoning"] = "Unsure.",
        });

        Ok(result);
        var row = await RowAsync(singleItem);
        row.Verdict.ShouldBe(ReviewVerdict.NeedsHuman);
        row.VerdictTopicLabel.ShouldBeNull();
        JsonDocument.Parse(row.VerdictFilterCriteria!).RootElement.GetProperty("from").GetString().ShouldBe("billing.example.com");
    }

    [Theory]
    [InlineData("alternative", null, null, "label path")]
    [InlineData("alternative", "INBOX", null, "label path")]
    [InlineData("maybe", null, null, "Verdict must be one of")]
    [InlineData("agree", null, "long", "Filter criteria")]
    public async Task Invalid_verdict_is_not_stored_and_says_why(string verdict, string? label, string? filter, string reason)
    {
        var result = await SubmitAsync(new()
        {
            ["id"] = singleItem.ToString(),
            ["verdict"] = verdict,
            ["topic_label"] = label,
            ["filter_criteria"] = filter is null ? null : new string('x', SubmitTools.MaxPlainFilterCriteriaLength + 1),
            ["reasoning"] = "Synthetic reasoning.",
        });

        Failed(result, "queued", reason);
        (await RowAsync(singleItem)).Verdict.ShouldBeNull();
        notifier.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("0197a000-0000-7000-8000-000000000000")]
    public async Task Unknown_id_is_ok_false_without_status(string id) =>
        Failed(await SubmitAsync(new() { ["id"] = id, ["verdict"] = "agree", ["reasoning"] = "Synthetic reasoning." }), null, "No review item");

    [Fact]
    public async Task Second_submission_and_a_cancelled_item_are_refused()
    {
        var agree = new Dictionary<string, object?> { ["id"] = singleItem.ToString(), ["verdict"] = "agree", ["reasoning"] = "First." };
        Ok(await SubmitAsync(agree));
        Failed(await SubmitAsync(new(agree) { ["verdict"] = "needs_human", ["reasoning"] = "Second." }), "reviewed", "already reviewed");
        (await RowAsync(singleItem)).ShouldSatisfyAllConditions(r => r.Verdict.ShouldBe(ReviewVerdict.Agree), r => r.Reasoning.ShouldBe("First."));

        (await h.PostAsync($"/api/claude/reviews/{groupItem}/cancel", new { })).EnsureSuccessStatusCode();
        Failed(await SubmitAsync(new(agree) { ["id"] = groupItem.ToString() }), "cancelled", "cancelled");
    }

    [Fact]
    public async Task Item_whose_suggestion_the_user_already_decided_is_skipped_by_the_list_and_refused()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.Id == c00).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SuggestionStatus.Approved), Ct);
        }

        var list = McpTestClient.Structured(await McpTestClient.CallAsync(client, "list_pending_reviews", Ct));
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ShouldBe([groupItem]);

        Failed(await SubmitAsync(new() { ["id"] = singleItem.ToString(), ["verdict"] = "agree", ["reasoning"] = "Synthetic reasoning." }),
            "queued", "already decided");
        (await RowAsync(singleItem)).Status.ShouldBe(ExternalReviewStatus.Queued);
    }

    private Task<CallToolResult> SubmitAsync(Dictionary<string, object?> arguments) =>
        McpTestClient.CallAsync(client, "submit_review", Ct, arguments);

    private static void Ok(CallToolResult result)
    {
        var json = McpTestClient.Structured(result);
        json.GetProperty("ok").GetBoolean().ShouldBeTrue(json.GetRawText());
        json.GetProperty("status").GetString().ShouldBe("reviewed");
    }

    private static void Failed(CallToolResult result, string? status, string reason)
    {
        var json = McpTestClient.Structured(result);
        json.GetProperty("ok").GetBoolean().ShouldBeFalse();
        json.GetProperty("status").ValueKind.ShouldBe(status is null ? JsonValueKind.Null : JsonValueKind.String);
        if (status is not null)
        {
            json.GetProperty("status").GetString().ShouldBe(status);
        }

        json.GetProperty("reason").GetString().ShouldNotBeNull().ShouldContain(reason, Case.Insensitive);
    }

    private async Task<ExternalReviewRow> RowAsync(Guid id)
    {
        await using var db = postgres.CreateDbContext();
        return await db.ExternalReviews.AsNoTracking().SingleAsync(r => r.Id == id, Ct);
    }

    private async Task<Guid> CreateAsync(CreateExternalReviewsRequest request)
    {
        var response = await h.PostAsync("/api/claude/reviews", request);
        response.EnsureSuccessStatusCode();
        var created = JsonSerializer.Deserialize<CreateExternalReviewsResponse>(await response.Content.ReadAsStringAsync(Ct), JsonSerializerOptions.Web)!;
        return created.Items.Single().Id;
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
