using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// #488: with the Claude API provider a run uses <see cref="AppSettings.ClaudeApiModel"/>, never warns near Ollama's
/// <c>num_ctx</c>, keeps the Claude client's error text, and refuses to start without a model or key.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisRunClaudeProviderTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Model = "test-model-a";
    private const string Key = "synthetic-test-key-0000";

    // Above 0.9 × the default num_ctx: an Ollama run would count every call as near the limit.
    private readonly FakeChatClient chat = new()
    {
        Responder = FakeAnalysisResponder.Answer,
        Usage = new UsageDetails { InputTokenCount = 7400, OutputTokenCount = 120 },
    };

    private readonly AnalysisRunHarness h;
    private readonly PostgresFixture postgres;
    private IChatClient active;

    public AnalysisRunClaudeProviderTests(ApiFactory factory, PostgresFixture postgres)
    {
        this.postgres = postgres;
        active = chat;
        h = new AnalysisRunHarness(factory, postgres)
        {
            ConfigureServices = services => services.AddScoped<ILlmClientFactory>(_ => new ScriptedLlmFactory(active)),
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        await UseClaudeAsync(Model, withKey: true);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Run_uses_the_claude_model_without_an_ollama_chat_model_and_counts_no_near_limit_calls()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        done.Status.ShouldBe("completed");
        done.Model.ShouldBe(Model);
        chat.Requests.Count.ShouldBeGreaterThan(0);
        done.PromptTokens.ShouldBe(chat.Requests.Count * 7400);
        done.NearContextLimit.ShouldBe(0);
    }

    [Fact]
    public async Task Start_without_a_claude_model_is_409_llm_not_configured()
    {
        await UseClaudeAsync(null, withKey: true);

        await ShouldBeNotConfiguredAsync(await h.PostAsync("/api/analysis/runs", new StartAnalysisRunRequest("inbox", null, null, 5, null)));
    }

    [Fact]
    public async Task Start_and_taxonomy_without_a_key_are_409_llm_not_configured()
    {
        await UseClaudeAsync(Model, withKey: false);

        await ShouldBeNotConfiguredAsync(await h.PostAsync("/api/analysis/runs", new StartAnalysisRunRequest("inbox", null, null, 5, null)));
        await ShouldBeNotConfiguredAsync(await h.PostWithoutBodyAsync("/api/rules/labels/taxonomy"));
        await using var db = postgres.CreateDbContext();
        (await db.Jobs.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Claude_api_failure_keeps_its_own_message_on_the_run()
    {
        const string message = "The Claude API is overloaded (HTTP 529). Try again later.";
        active = new ThrowingChatClient(new HttpRequestException(message, null, (HttpStatusCode)529));
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        done.Status.ShouldBe("failed");
        done.Error.ShouldBe(message);
    }

    private async Task UseClaudeAsync(string? model, bool withKey)
    {
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(
            x => x with { LlmProvider = LlmProvider.ClaudeApi, ClaudeApiModel = model, ChatModel = null }, Ct);
        var keys = scope.ServiceProvider.GetRequiredService<ClaudeApiKeyService>();
        await (withKey ? keys.SetAsync(Key, Ct) : keys.ClearAsync(Ct));
    }

    private static async Task ShouldBeNotConfiguredAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title.ShouldBe("LLM not configured");
    }

    private sealed class ThrowingChatClient(Exception error) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(error);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw error;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
