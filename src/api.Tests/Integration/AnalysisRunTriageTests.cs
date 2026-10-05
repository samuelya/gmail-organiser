using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Llm;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>#375: the triage model answers first; the chat model repeats the prompt on low confidence or invalid output.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisRunTriageTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string TriageModel = "fake-triage-model";
    private const string ChatModel = AnalysisRunHarness.ChatModel;

    private readonly ScriptedChatClient triage = new();
    private readonly PostgresFixture postgres;
    private readonly AnalysisRunHarness h;

    public AnalysisRunTriageTests(ApiFactory factory, PostgresFixture postgres)
    {
        this.postgres = postgres;
        h = new AnalysisRunHarness(factory, postgres)
        {
            ConfigureServices = services => services.AddScoped<ILlmClientFactory>(
                _ => new ByModelLlmFactory(new ScriptedLlmFactory(h!.Chat), new Dictionary<string, IChatClient> { [TriageModel] = triage })),
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => h.InitializeAsync();

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Confident_triage_answers_stay_on_the_triage_model()
    {
        await UseTriageAsync();
        triage.Respond = (ids, _, _, _) => Task.FromResult(Answer(ids, 0.9));

        var done = await RunAsync();

        (done.LlmCalls, done.TriageCalls, done.EscalatedCalls).ShouldBe((3, 3, 0));
        h.Chat.Calls.ShouldBe(0);
        done.Model.ShouldBe(ChatModel);
        var rows = await RowsAsync();
        rows.Count.ShouldBe(20);
        rows.ShouldAllBe(s => s.Model == TriageModel);
    }

    [Fact]
    public async Task Low_confidence_repeats_the_same_prompt_with_the_chat_model()
    {
        await UseTriageAsync();
        triage.Respond = (ids, _, _, _) => Task.FromResult(Answer(ids, 0.69));

        var done = await RunAsync();

        (done.LlmCalls, done.TriageCalls, done.EscalatedCalls).ShouldBe((6, 3, 3));
        h.Chat.Calls.ShouldBe(3);
        Texts(h.Chat.Requests).ShouldBe(Texts(triage.Requests));
        (await RowsAsync()).ShouldAllBe(s => s.Model == ChatModel);
    }

    [Fact]
    public async Task Invalid_triage_output_escalates_and_both_calls_are_counted()
    {
        await UseTriageAsync();
        triage.Respond = (_, _, _, _) => Task.FromResult("not json");

        var done = await RunAsync();

        (done.LlmCalls, done.TriageCalls, done.EscalatedCalls, done.FailedMessages).ShouldBe((6, 3, 3, 0));
        (await RowsAsync()).ShouldAllBe(s => s.Model == ChatModel);
    }

    [Fact]
    public async Task Confident_triage_answer_fills_only_an_email_the_chat_model_left_out()
    {
        await UseTriageAsync();
        // The first email of each prompt is confident; the chat model never answers it, so its retry is spent too.
        triage.Respond = (ids, _, _, _) => Task.FromResult(Answer(ids, 0.5, first: 0.9));
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Agree([.. ids.Skip(1)]));

        var done = await RunAsync();

        (done.TriageCalls, done.EscalatedCalls, done.LlmCalls, done.FailedMessages).ShouldBe((3, 3, 9, 0));
        var rows = await RowsAsync();
        var llm = rows.Where(s => s.Source == SuggestionSource.Llm).ToList();
        llm.Count(s => s.Model == TriageModel).ShouldBe(3);
        llm.Count(s => s.Model == ChatModel).ShouldBe(6);
        rows.Where(s => s.Source == SuggestionSource.Derived).ShouldAllBe(s => s.Model == ChatModel);
    }

    [Fact]
    public async Task Without_a_triage_model_only_the_chat_model_is_asked()
    {
        var done = await RunAsync();

        (done.LlmCalls, done.TriageCalls, done.EscalatedCalls).ShouldBe((3, 0, 0));
        triage.Calls.ShouldBe(0);
        (await RowsAsync()).ShouldAllBe(s => s.Model == ChatModel);
    }

    [Fact]
    public async Task Triage_settings_round_trip_and_an_empty_model_turns_triage_off()
    {
        var saved = await PutSettingsAsync(new { triageModel = $" {TriageModel} ", triageConfidenceThreshold = 0.55 });
        saved.GetProperty("triageModel").GetString().ShouldBe(TriageModel);
        saved.GetProperty("triageConfidenceThreshold").GetDouble().ShouldBe(0.55);

        var off = await PutSettingsAsync(new { triageModel = "" });
        off.GetProperty("triageModel").ValueKind.ShouldBe(JsonValueKind.Null);
        off.GetProperty("triageConfidenceThreshold").GetDouble().ShouldBe(0.55);
    }

    [Theory]
    [InlineData(-0.01, true)]
    [InlineData(0.0, false)]
    [InlineData(1.0, false)]
    [InlineData(1.01, true)]
    [InlineData(double.NaN, true)]
    public void Threshold_is_validated_between_zero_and_one(double value, bool invalid) =>
        SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, TriageConfidenceThreshold: value))
            .ContainsKey("triageConfidenceThreshold").ShouldBe(invalid);

    [Fact]
    public void Triage_defaults_to_off_with_a_threshold_of_0_7()
    {
        var settings = new AppSettings();
        (settings.TriageModel, settings.TriageConfidenceThreshold).ShouldBe((null, 0.7));
        SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, TriageModel: new string('m', 300)))
            .ShouldContainKey("triageModel");
    }

    private async Task UseTriageAsync()
    {
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(x => x with { TriageModel = TriageModel }, Ct);
    }

    private async Task<AnalysisRunDto> RunAsync()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        var done = await h.GetRunAsync(run.Id);
        done.Status.ShouldBe("completed");
        return done;
    }

    private async Task<List<SuggestionRow>> RowsAsync()
    {
        await using var db = postgres.CreateDbContext();
        return await db.Suggestions.AsNoTracking().ToListAsync(Ct);
    }

    private async Task<JsonElement> PutSettingsAsync(object body)
    {
        var response = await h.PutAsync("/api/settings", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static List<string> Texts(IEnumerable<IReadOnlyList<ChatMessage>> requests) =>
        [.. requests.Select(r => string.Join('\n', r.Select(m => m.Text)))];

    private static string Answer(IReadOnlyList<string> ids, double confidence, double? first = null) => JsonSerializer.Serialize(new
    {
        suggestions = ids.Select((id, i) => new
        {
            id,
            topicLabel = AnalysisRunHarness.LabelFor(id),
            isNewLabel = false,
            needsAction = false,
            toBeDeleted = false,
            unsubscribeSuggested = false,
            confidence = i == 0 && first is { } f ? f : confidence,
            reason = "Synthetic reason",
        }),
    });

    /// <summary>The chat model from <paramref name="inner"/>; an explicit model from <paramref name="byModel"/> when listed.</summary>
    private sealed class ByModelLlmFactory(ILlmClientFactory inner, IReadOnlyDictionary<string, IChatClient> byModel) : ILlmClientFactory
    {
        public Task<IChatClient> CreateChatClientAsync(CancellationToken ct = default) => inner.CreateChatClientAsync(ct);

        public Task<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(CancellationToken ct = default) =>
            inner.CreateEmbeddingGeneratorAsync(ct);

        public IChatClient CreateChatClient(Uri baseUrl, string model) =>
            byModel.TryGetValue(model, out var chat) ? chat : inner.CreateChatClient(baseUrl, model);

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(Uri baseUrl, string model) =>
            inner.CreateEmbeddingGenerator(baseUrl, model);
    }
}
