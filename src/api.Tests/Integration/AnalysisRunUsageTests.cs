using GmailOrganiser.Analysis;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>#353: every analysis call sends <c>num_ctx</c>, and its tokens, time and near-limit count land on the run.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisRunUsageTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const long PromptTokens = 7400;
    private const long CompletionTokens = 120;

    private readonly FakeChatClient chat = new()
    {
        Responder = FakeAnalysisResponder.Answer,
        Usage = new UsageDetails { InputTokenCount = PromptTokens, OutputTokenCount = CompletionTokens },
    };

    private readonly AnalysisRunHarness h;

    public AnalysisRunUsageTests(ApiFactory factory, PostgresFixture postgres)
    {
        h = new AnalysisRunHarness(factory, postgres)
        {
            ConfigureServices = services => services.AddScoped<ILlmClientFactory>(sp => new ScriptedLlmFactory(sp, chat)),
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => h.InitializeAsync();

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Run_sums_the_usage_of_its_calls_and_counts_prompts_near_the_default_context()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        done.Status.ShouldBe("completed");
        var calls = chat.Requests.Count;
        calls.ShouldBeGreaterThan(0);
        done.LlmCalls.ShouldBe(calls);
        chat.Requests.ShouldAllBe(r => (int)r.Options!.AdditionalProperties![LlmCallMeter.NumCtxKey]! == AppSettings.DefaultLlmNumCtx);
        (done.PromptTokens, done.CompletionTokens).ShouldBe((calls * PromptTokens, calls * CompletionTokens));
        // 7400 ≥ 0.9 × 8192: every call is near the limit.
        done.NearContextLimit.ShouldBe(calls);
        done.LlmMilliseconds.ShouldBeGreaterThanOrEqualTo(0);
        done.LlmSeconds.ShouldBe(done.LlmMilliseconds / 1000.0, 1e-9);
    }

    [Fact]
    public async Task Saved_num_ctx_reaches_every_call()
    {
        await using (var scope = h.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(x => x with { LlmNumCtx = 16_384 }, Ct);
        }

        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        done.Status.ShouldBe("completed");
        chat.Requests.ShouldAllBe(r => (int)r.Options!.AdditionalProperties![LlmCallMeter.NumCtxKey]! == 16_384);
        done.NearContextLimit.ShouldBe(0);
        done.PromptTokens.ShouldBe(chat.Requests.Count * PromptTokens);
    }
}
