using System.Net;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Mcp;
using GmailOrganiser.Review;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// The read tools (#164) over the MCP transport, against a finished inbox run of the harness mailbox: the shop group
/// (10 members) and the single billing suggestion <c>c00</c> are queued for review.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class McpReviewToolsTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly PostgresFixture postgres;
    private readonly AnalysisRunHarness h;
    private McpClient client = null!;
    private Guid groupItem;
    private Guid singleItem;

    public McpReviewToolsTests(ApiFactory factory, PostgresFixture postgres)
    {
        this.postgres = postgres;
        h = new(factory, postgres);
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
        Guid c00;
        await using (var db = postgres.CreateDbContext())
        {
            shopKey = await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Shop && s.GroupKey != null)
                .Select(s => s.GroupKey!).Distinct().SingleAsync(Ct);
            c00 = await db.Suggestions.Where(s => s.MessageId == "c00").Select(s => s.Id).SingleAsync(Ct);
        }

        // Two requests, so the group is the older item.
        groupItem = await CreateAsync(new(null, [new GroupRef(AnalysisRunHarness.Shop, shopKey)], null));
        singleItem = await CreateAsync(new([c00], null, null));
        client = await McpTestClient.ConnectAsync(h.Host, Ct);
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
    public async Task List_pending_reviews_returns_queued_items_oldest_first_with_the_local_suggestion()
    {
        var list = McpTestClient.Structured(await McpTestClient.CallAsync(client, "list_pending_reviews", Ct));

        list.GetProperty("status").GetString().ShouldBe("queued");
        var items = list.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("id").GetGuid()).ShouldBe([groupItem, singleItem]);

        var group = items[0];
        group.GetProperty("targetType").GetString().ShouldBe("group");
        group.GetProperty("sender").GetString().ShouldBe(AnalysisRunHarness.Shop);
        group.GetProperty("groupDisplay").GetString().ShouldBe("Weekly offer 1");
        group.GetProperty("memberCount").GetInt32().ShouldBe(10);
        var local = group.GetProperty("local");
        local.GetProperty("topicLabel").GetString().ShouldBe("Shopping");
        local.GetProperty("source").GetString().ShouldBe("llm");
        local.GetProperty("confidence").GetDouble().ShouldBe(0.9);
        local.GetProperty("reason").GetString().ShouldBe("Synthetic reason");
        local.GetProperty("isNewLabel").GetBoolean().ShouldBeFalse();

        items[1].GetProperty("targetType").GetString().ShouldBe("suggestion");
        items[1].GetProperty("memberCount").GetInt32().ShouldBe(1);
        items[1].GetProperty("local").GetProperty("topicLabel").GetString().ShouldBe("Finance");

        var limited = McpTestClient.Structured(await McpTestClient.CallAsync(client, "list_pending_reviews", Ct, new Dictionary<string, object?> { ["limit"] = 1 }));
        limited.GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task List_pending_reviews_returns_only_running_items_while_a_run_is_in_progress()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.ExternalReviews.Where(r => r.Id == singleItem)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ExternalReviewStatus.Running), Ct);
        }

        var list = McpTestClient.Structured(await McpTestClient.CallAsync(client, "list_pending_reviews", Ct));

        list.GetProperty("status").GetString().ShouldBe("running");
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ShouldBe([singleItem]);
    }

    [Fact]
    public async Task Get_review_item_returns_representatives_first_sender_stats_and_bodies_only_on_request()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Senders.Where(s => s.Address == AnalysisRunHarness.Shop)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Allowlisted, true), Ct);
        }

        string[] representatives;
        await using (var db = postgres.CreateDbContext())
        {
            representatives = await db.Suggestions
                .Where(s => s.SenderAddress == AnalysisRunHarness.Shop && s.Source == SuggestionSource.Llm)
                .Select(s => s.MessageId).ToArrayAsync(Ct);
        }

        var item = McpTestClient.Structured(await McpTestClient.CallAsync(client, "get_review_item", Ct,
            new Dictionary<string, object?> { ["id"] = groupItem.ToString() }));

        item.GetProperty("status").GetString().ShouldBe("queued");
        item.GetProperty("item").GetProperty("local").GetProperty("topicLabel").GetString().ShouldBe("Shopping");
        var sender = item.GetProperty("senderStats");
        sender.GetProperty("totalCount").GetInt32().ShouldBe(10);
        sender.GetProperty("allowlisted").GetBoolean().ShouldBeTrue();
        var samples = item.GetProperty("samples").EnumerateArray().ToList();
        samples.Count.ShouldBe(ReviewItemBuilder.MaxSamples);
        representatives.Length.ShouldBeGreaterThan(0);
        samples.Take(Math.Min(representatives.Length, samples.Count)).Select(s => s.GetProperty("id").GetString())
            .ShouldBeSubsetOf(representatives);
        samples[0].GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ShouldContain("INBOX");
        samples.ShouldAllBe(s => s.GetProperty("body").ValueKind == JsonValueKind.Null);
        item.GetProperty("labelTree").ValueKind.ShouldBe(JsonValueKind.Array);
        item.GetProperty("similarDecisions").ValueKind.ShouldBe(JsonValueKind.Array);

        var withBodies = McpTestClient.Structured(await McpTestClient.CallAsync(client, "get_review_item", Ct,
            new Dictionary<string, object?> { ["id"] = groupItem.ToString(), ["include_bodies"] = true }));
        withBodies.GetProperty("samples").EnumerateArray()
            .ShouldAllBe(s => s.GetProperty("body").GetString()!.Contains(AnalysisRunHarness.BodyMarker));
    }

    [Fact]
    public async Task Get_review_item_lists_similar_past_decisions_of_the_sender()
    {
        Guid c01;
        await using (var db = postgres.CreateDbContext())
        {
            c01 = await db.Suggestions.Where(s => s.MessageId == "c01").Select(s => s.Id).SingleAsync(Ct);
        }

        (await h.PostWithoutBodyAsync($"/api/review/suggestions/{c01}/approve")).EnsureSuccessStatusCode();

        var item = McpTestClient.Structured(await McpTestClient.CallAsync(client, "get_review_item", Ct,
            new Dictionary<string, object?> { ["id"] = singleItem.ToString() }));

        item.GetProperty("samples").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ShouldBe(["c00"]);
        var similar = item.GetProperty("similarDecisions").EnumerateArray().ToList();
        similar.ShouldNotBeEmpty();
        similar[0].GetProperty("senderAddress").GetString().ShouldBe(AnalysisRunHarness.Billing);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    public async Task Get_review_item_with_an_unknown_id_is_an_error_result(string id)
    {
        var result = await McpTestClient.CallAsync(client, "get_review_item", Ct, new Dictionary<string, object?> { ["id"] = id });

        McpTestClient.ErrorText(result).ShouldContain("No review item");
    }

    [Fact]
    public async Task Get_label_tree_nests_labels_with_message_counts_and_marks_the_configured_labels()
    {
        string shopping;
        await using (var scope = h.Services.CreateAsyncScope())
        {
            var gmail = scope.ServiceProvider.GetRequiredService<IGmailClient>();
            shopping = (await gmail.CreateLabelAsync("Shopping", Ct)).Id;
            await gmail.CreateLabelAsync("Finance/Invoices", Ct);
            await gmail.CreateLabelAsync("Action/ToDo", Ct);
            await gmail.CreateLabelAsync("To-Be-Deleted", Ct);
            scope.ServiceProvider.GetRequiredService<LabelCatalog>().Invalidate();
        }

        await using (var db = postgres.CreateDbContext())
        {
            foreach (var message in await db.Messages.Where(m => m.FromAddress == AnalysisRunHarness.Shop).ToListAsync(Ct))
            {
                message.LabelIds = [.. message.LabelIds, shopping];
            }

            await db.SaveChangesAsync(Ct);
        }

        var tree = McpTestClient.Structured(await McpTestClient.CallAsync(client, "get_label_tree", Ct));

        tree.GetProperty("labelCount").GetInt32().ShouldBe(FakeLabelStore.SeedUserLabelNames.Count + 4);
        var roots = tree.GetProperty("labels").EnumerateArray().ToDictionary(n => n.GetProperty("name").GetString()!);
        roots.Keys.Order(StringComparer.Ordinal).ShouldBe(["Action", "Example", "Finance", "Shopping", "Synthetic Receipts", "To-Be-Deleted"]);
        roots["Example"].GetProperty("children").EnumerateArray().Single().GetProperty("path").GetString().ShouldBe("Example/Nested");
        roots["Shopping"].GetProperty("messageCount").GetInt32().ShouldBe(10);
        roots["Shopping"].GetProperty("id").GetString().ShouldBe(shopping);
        roots["To-Be-Deleted"].GetProperty("isDeleteLabel").GetBoolean().ShouldBeTrue();
        roots["Finance"].GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
        var invoices = roots["Finance"].GetProperty("children").EnumerateArray().Single();
        invoices.GetProperty("path").GetString().ShouldBe("Finance/Invoices");
        var todo = roots["Action"].GetProperty("children").EnumerateArray().Single();
        todo.GetProperty("isActionLabel").GetBoolean().ShouldBeTrue();
        roots["Action"].GetProperty("isActionLabel").GetBoolean().ShouldBeFalse();

        var item = McpTestClient.Structured(await McpTestClient.CallAsync(client, "get_review_item", Ct,
            new Dictionary<string, object?> { ["id"] = groupItem.ToString() }));
        item.GetProperty("labelTree").EnumerateArray().Select(l => l.GetString())
            .ShouldBe([.. FakeLabelStore.SeedUserLabelNames.Concat(["Action/ToDo", "Finance/Invoices", "Shopping", "To-Be-Deleted"]).Order(StringComparer.Ordinal)]);
        item.GetProperty("samples").EnumerateArray().First()
            .GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ShouldContain("Shopping");
    }

    [Fact]
    public async Task Get_review_item_without_bodies_stays_readable_when_the_Gmail_label_fetch_fails()
    {
        var gmail = h.Services.GetRequiredService<FakeGmailClient>();
        h.Services.GetRequiredService<LabelCatalog>().Invalidate();
        gmail.FailNext(HttpStatusCode.BadRequest, 10);

        (await McpTestClient.CallAsync(client, "get_label_tree", Ct)).IsError.ShouldBe(true);
        var result = await McpTestClient.CallAsync(client, "get_review_item", Ct,
            new Dictionary<string, object?> { ["id"] = groupItem.ToString() });
        gmail.FailNext(HttpStatusCode.BadRequest, 0);

        result.IsError.ShouldNotBe(true);
        var item = McpTestClient.Structured(result);
        item.GetProperty("labelTree").GetArrayLength().ShouldBe(0);
        item.GetProperty("samples").GetArrayLength().ShouldBeGreaterThan(0);
    }

    private async Task<Guid> CreateAsync(CreateExternalReviewsRequest request)
    {
        var response = await h.PostAsync("/api/claude/reviews", request);
        response.EnsureSuccessStatusCode();
        var created = JsonSerializer.Deserialize<CreateExternalReviewsResponse>(await response.Content.ReadAsStringAsync(Ct), JsonSerializerOptions.Web)!;
        return created.Items.Single().Id;
    }
}
