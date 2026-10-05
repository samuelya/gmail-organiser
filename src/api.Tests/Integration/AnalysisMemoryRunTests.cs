using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Memory;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The memory short-circuit and memory hints in a run (#111), on the run harness's synthetic mailbox.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisMemoryRunTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string StarredId = "a03";
    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        await using var db = postgres.CreateDbContext();
        var at = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var scope = GroupKey.For(await db.Messages.AsNoTracking().SingleAsync(m => m.Id == "a00", Ct));
        db.Decisions.AddRange(Enumerable.Range(0, 3).Select(i => new DecisionRow
        {
            Id = Guid.NewGuid(),
            MessageId = $"seed-{i}",
            SenderAddress = AnalysisRunHarness.Shop,
            ScopeKey = scope,
            SubjectTemplate = SubjectNormaliser.Template($"Weekly offer {i + 1}"),
            TopicLabel = "Deals",
            Outcome = DecisionOutcome.Approved,
            Source = SuggestionSource.Llm,
            CreatedAt = at.AddHours(i),
        }));
        await db.SaveChangesAsync(Ct);
        await db.Messages.Where(m => m.Id == StarredId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, new[] { "INBOX", MessageProtection.StarredLabel }), Ct);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Approved_sender_pattern_covers_the_group_from_memory_and_protected_mail_goes_to_the_model()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        done.Status.ShouldBe("completed");
        (done.MessagesCovered, done.MessagesFromMemory, done.MessagesLlm, done.MessagesDerived).ShouldBe((20, 9, 7, 4));
        done.LlmCalls.ShouldBe(3); // news, billing, and the starred shop email alone
        h.Chat.Requests.ShouldContain(r => r.Any(m => m.Text.Contains($"id: {StarredId}")));

        await using var db = postgres.CreateDbContext();
        var shop = await db.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Shop).ToListAsync(Ct);
        var memory = shop.Where(s => s.Source == SuggestionSource.Memory).ToList();
        memory.Count.ShouldBe(9);
        memory.ShouldAllBe(s => s.TopicLabel == "Deals" && s.IsNewLabel && s.Reason == "Matches 3 approved decisions for this sender"
            && Math.Abs(s.Confidence - (1 - AppSettings.DefaultAnalysisDerivedConfidencePenalty)) < 1e-9);
        shop.Single(s => s.MessageId == StarredId).Source.ShouldBe(SuggestionSource.Llm);
    }

    [Fact]
    public async Task Memory_suggestion_of_an_existing_Gmail_label_is_not_a_new_label()
    {
        var existing = FakeLabelStore.SeedUserLabelNames[^1];
        await using (var db = postgres.CreateDbContext())
        {
            await db.Decisions.ExecuteUpdateAsync(s => s.SetProperty(d => d.TopicLabel, existing.ToLowerInvariant()), Ct);
        }

        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();

        (await h.GetRunAsync(run.Id)).MessagesFromMemory.ShouldBe(9);
        await using var check = postgres.CreateDbContext();
        (await check.Suggestions.AsNoTracking().Where(s => s.Source == SuggestionSource.Memory).Select(s => s.IsNewLabel).ToListAsync(Ct))
            .ShouldAllBe(isNew => !isNew);
    }

    [Fact]
    public async Task Labelled_run_shows_current_labels_and_memory_answers_only_where_they_match()
    {
        // Label_1 is the first seeded user label, Label_3 the last; Label_99 was deleted in Gmail since the fetch.
        var (first, last) = (FakeLabelStore.SeedUserLabelNames[0], FakeLabelStore.SeedUserLabelNames[^1]);
        await using (var db = postgres.CreateDbContext())
        {
            await db.Decisions.ExecuteUpdateAsync(s => s.SetProperty(d => d.TopicLabel, last.ToLowerInvariant()), Ct);
            string[] filedFirst = ["a01", "a02", StarredId, "a04"];
            await db.Messages.Where(m => filedFirst.Contains(m.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, new[] { "INBOX", "Label_1" }), Ct);
            await db.Messages.Where(m => m.Id == "a00")
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, new[] { "Label_99", "Label_1" }), Ct);
            await db.Messages.Where(m => m.Id.StartsWith("a0") && !filedFirst.Contains(m.Id) && m.Id != "a00")
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, new[] { "INBOX", "Label_3" }), Ct);
        }

        var run = await h.StartAsync(new StartAnalysisRunRequest("labelled", null, null, 20, null));
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.MessagesFromMemory).ShouldBe(("completed", 10, 5));
        h.Chat.Requests.ShouldNotContain(r => r.Any(m => m.Text.Contains("id: b00")));
        h.Chat.Requests.Single(r => r.Any(m => m.Text.Contains("id: a00")))
            .ShouldContain(m => m.Text.Contains($"current labels: {first}\n"));
        await using var check = postgres.CreateDbContext();
        (await check.Suggestions.AsNoTracking().Where(s => s.Source == SuggestionSource.Memory).Select(s => s.MessageId)
            .OrderBy(id => id).ToListAsync(Ct)).ShouldBe(["a05", "a06", "a07", "a08", "a09"]);
    }

    [Fact]
    public async Task With_the_short_circuit_off_the_model_sees_the_sender_decisions_as_memory()
    {
        await using (var scope = h.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
                .UpdateAsync(s => s with { AnalysisMemoryShortCircuit = false }, Ct);
        }

        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.MessagesFromMemory, done.LlmCalls).ShouldBe((0, 3));
        var shopPrompt = h.Chat.Requests.Single(r => r.Any(m => m.Text.Contains("id: a00")));
        // No embedding model in the harness: the exact-sender fill, deduplicated to one hint.
        shopPrompt.Count(m => m.Text.Contains($"sender: {AnalysisRunHarness.Shop} |")).ShouldBe(1);
        shopPrompt.ShouldContain(m => m.Text.Contains("topicLabel: Deals | type: ? | mailType: - | needsAction: no | toBeDeleted: no | outcome: approved | similarity: 1.00"));
        h.Chat.Requests.Single(r => r.Any(m => m.Text.Contains("id: b00")))
            .ShouldNotContain(m => m.Text.Contains("topicLabel: Deals"));
    }

    [Fact]
    public async Task Preview_estimates_the_memory_coverage_with_the_same_lookup()
    {
        var response = await h.PostAsync("/api/analysis/preview", new { scope = "inbox", count = 20 });
        var preview = (await response.Content.ReadFromJsonAsync<GroupingPreviewDto>(Ct)).ShouldNotBeNull();

        (preview.Messages, preview.Groups).ShouldBe((20, 3));
        (preview.EstimatedFromMemory, preview.EstimatedLlmCalls, preview.EstimatedDerived).ShouldBe((9, 3, 4));
        h.Chat.Calls.ShouldBe(0);
    }
}
