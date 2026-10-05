using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Memory;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// #376: every harness email from its own one-off sender, so an inbox run of 20 is 20 singletons: packs of 16 and 4,
/// snippet-only, with low-confidence and unusable answers asked again one by one with the body.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisRunPackingTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string LowConfidenceId = "a03";
    private const string Blocked = "Synthetic Blocked";

    private readonly AnalysisRunHarness h = new(factory, postgres)
    {
        Customise = m => m with { From = $"one-off-{m.Id}@example.com" },
    };

    private readonly ConcurrentQueue<string> bodies = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        await using var db = postgres.CreateDbContext();
        await db.Messages.ExecuteUpdateAsync(s => s.SetProperty(m => m.Snippet, m => "Synthetic snippet of " + m.Id), Ct);
        h.Gmail.BeforeBody = (id, _) =>
        {
            bodies.Enqueue(id);
            return Task.CompletedTask;
        };
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(Answer(ids, id => ids.Count > 1 && id == LowConfidenceId ? 0.3 : 0.9));
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Twenty_singletons_take_two_pack_calls_and_a_low_confidence_answer_is_asked_again_with_the_body()
    {
        var preview = await PreviewAsync();
        (preview.Packs, preview.EstimatedLlmCalls).ShouldBe((2, 2));

        var done = await RunAsync();

        (done.LlmCalls, done.PackedMessages, done.PackRetries, done.MessagesCovered, done.MessagesLlm, done.FailedMessages)
            .ShouldBe((3, 20, 1, 20, 20, 0));
        var requests = h.Chat.Requests.Select(Text).ToList();
        // The retry comes right after its pack, before the next pack.
        new[] { requests[0], requests[2] }.ShouldAllBe(r => r.Contains(AnalysisRunJob.SnippetOnlyMarker) && !r.Contains(AnalysisRunHarness.BodyMarker));
        requests[0].ShouldContain("Synthetic snippet of a00");
        requests[1].ShouldContain($"Synthetic body of {LowConfidenceId}");
        bodies.ShouldBe([LowConfidenceId]);

        await using var db = postgres.CreateDbContext();
        var rows = await db.Suggestions.AsNoTracking().ToListAsync(Ct);
        rows.Count.ShouldBe(20);
        rows.ShouldAllBe(s => s.FilterCriteria == null && s.GroupKey == null && s.Source == SuggestionSource.Llm);
    }

    [Fact]
    public async Task Pack_size_one_analyses_every_singleton_on_its_own()
    {
        await SettingsAsync(s => s with { AnalysisPackSize = 1 });

        (await PreviewAsync()).Packs.ShouldBe(0);
        var done = await RunAsync();

        (done.LlmCalls, done.PackedMessages, done.PackRetries, done.MessagesCovered).ShouldBe((20, 0, 0, 20));
        h.Chat.Requests.ShouldAllBe(r => !Text(r).Contains(AnalysisRunJob.SnippetOnlyMarker));
        bodies.Count.ShouldBe(20);
    }

    [Fact]
    public async Task Unusable_pack_output_sends_the_whole_pack_to_individual_calls()
    {
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(ids.Count > 1 ? "not json" : Answer(ids, _ => 0.9));

        var done = await RunAsync();

        // Each pack: the call and its retry; then every member alone.
        (done.LlmCalls, done.PackedMessages, done.PackRetries, done.MessagesCovered, done.FailedMessages).ShouldBe((24, 20, 20, 20, 0));
        bodies.Count.ShouldBe(20);
    }

    [Fact]
    public async Task Packed_answers_pass_the_approved_label_set()
    {
        await SettingsAsync(s => s with { TaxonomyLocked = true, AnalysisMaxNewLabelsPerRun = 1, AnalysisBlockedLabels = [Blocked] });
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(Answer(ids, _ => 0.9, id => id.StartsWith('c') ? $"Finance/{Blocked}" : null));

        var done = await RunAsync();

        // Pack 1 (shop and news) is answered; pack 2 (billing) stays blocked, and so does each billing email alone.
        (done.LlmCalls, done.PackedMessages, done.PackRetries, done.MessagesCovered, done.FailedMessages).ShouldBe((11, 20, 4, 16, 4));
        await using var db = postgres.CreateDbContext();
        var rows = await db.Suggestions.AsNoTracking().ToListAsync(Ct);
        rows.ShouldAllBe(s => !s.MessageId.StartsWith('c') && s.ProposedNewLabel == s.TopicLabel);
        // The cap still applies to packed answers, without sending capped ones back to the model.
        var capped = rows.Where(s => s.Reason.EndsWith(ApprovedLabelSet.CapNote)).ToList();
        capped.Select(s => s.TopicLabel).Distinct().ShouldHaveSingleItem();
        capped.ShouldAllBe(s => s.Confidence <= ApprovedLabelSet.CappedConfidence);
    }

    [Fact]
    public async Task A_resume_asks_the_retry_alone_and_counts_each_pack_once()
    {
        // The first pack is stored with one retry; the next call (the retry) breaks the run.
        h.Chat.Respond = (ids, _, _, _) => ids.Count == 1 || ids.Any(id => id.StartsWith('c'))
            ? throw new InvalidOperationException("Synthetic model outage")
            : Task.FromResult(Answer(ids, id => id == LowConfidenceId ? 0.3 : 0.9));
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        var failed = await h.GetRunAsync(run.Id);
        (failed.Status, failed.PackedMessages, failed.PackRetries, failed.MessagesCovered).ShouldBe(("failed", 16, 1, 15));

        var before = h.Chat.Requests.Count;
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(Answer(ids, _ => 0.9));
        (await h.PostWithoutBodyAsync($"/api/analysis/runs/{run.Id}/resume")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.LlmCalls, done.PackedMessages, done.PackRetries, done.MessagesCovered).ShouldBe(("completed", 3, 20, 1, 20));
        var resumed = h.Chat.Requests.Skip(before).Select(Text).ToList();
        resumed.Count.ShouldBe(2);
        resumed[0].ShouldContain($"Synthetic body of {LowConfidenceId}");
        resumed[1].ShouldContain(AnalysisRunJob.SnippetOnlyMarker);
    }

    [Fact]
    public async Task Memory_runs_before_packing_so_the_model_bound_leftovers_share_one_pack()
    {
        // Memory covers all but the run's newest (pack 1) and oldest (pack 2) email: packed first, each would go alone.
        await using (var db = postgres.CreateDbContext())
        {
            var messages = await db.Messages.AsNoTracking().OrderByDescending(m => m.InternalDate).ThenBy(m => m.Id).Take(20).ToListAsync(Ct);
            var at = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
            db.Decisions.AddRange(messages.Skip(1).SkipLast(1).SelectMany(m => Enumerable.Range(0, 3).Select(i => new DecisionRow
            {
                Id = Guid.NewGuid(),
                MessageId = $"seed-{m.Id}-{i}",
                SenderAddress = m.FromAddress,
                ScopeKey = GroupKey.For(m),
                SubjectTemplate = SubjectNormaliser.Template(m.Subject),
                TopicLabel = "Synthetic Remembered",
                Outcome = DecisionOutcome.Approved,
                Source = SuggestionSource.Llm,
                CreatedAt = at.AddHours(i),
            })));
            await db.SaveChangesAsync(Ct);
        }

        var preview = await PreviewAsync();
        (preview.Packs, preview.EstimatedLlmCalls).ShouldBe((1, 1));

        var done = await RunAsync();

        (done.LlmCalls, done.PackedMessages, done.PackRetries, done.MessagesFromMemory, done.MessagesLlm).ShouldBe((1, 2, 0, 18, 2));
        h.Chat.Requests.Select(Text).ShouldHaveSingleItem().ShouldContain(AnalysisRunJob.SnippetOnlyMarker);
        bodies.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_low_confidence_pack_answer_is_stored_when_the_retry_alone_stays_invalid()
    {
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(ids.Count > 1 ? Answer(ids, id => id == LowConfidenceId ? 0.3 : 0.9) : "not json");

        var done = await RunAsync();

        // Two packs, then the retry and its repeat; the pack's own answer fills in.
        (done.LlmCalls, done.PackRetries, done.MessagesCovered, done.MessagesLlm, done.FailedMessages).ShouldBe((4, 1, 20, 20, 0));
        bodies.ShouldBe([LowConfidenceId]);
        await using var db = postgres.CreateDbContext();
        var row = await db.Suggestions.AsNoTracking().SingleAsync(s => s.MessageId == LowConfidenceId, Ct);
        (row.Source, row.Confidence, row.TopicLabel).ShouldBe((SuggestionSource.Llm, 0.3, AnalysisRunHarness.LabelFor(LowConfidenceId)));
    }

    private async Task<AnalysisRunDto> RunAsync()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        var done = await h.GetRunAsync(run.Id);
        done.Status.ShouldBe("completed");
        return done;
    }

    private async Task<GroupingPreviewDto> PreviewAsync()
    {
        var response = await h.PostAsync("/api/analysis/preview", new AnalysisPreviewRequest("inbox", null, 20, null));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<GroupingPreviewDto>(Ct)).ShouldNotBeNull();
    }

    private async Task SettingsAsync(Func<AppSettings, AppSettings> change)
    {
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(change, Ct);
    }

    private static string Text(IReadOnlyList<ChatMessage> request) => string.Join('\n', request.Select(m => m.Text));

    private static string Answer(IReadOnlyList<string> ids, Func<string, double> confidence, Func<string, string?>? label = null) =>
        JsonSerializer.Serialize(new
        {
            suggestions = ids.Select(id => new
            {
                id,
                topicLabel = label?.Invoke(id) ?? AnalysisRunHarness.LabelFor(id),
                mailType = "notification",
                needsAction = false,
                toBeDeleted = false,
                unsubscribeSuggested = false,
                confidence = confidence(id),
                reason = "Synthetic reason",
            }),
        });
}
