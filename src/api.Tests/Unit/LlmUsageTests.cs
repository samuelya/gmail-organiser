using System.Net;
using System.Text;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OllamaSharp;

namespace GmailOrganiser.Tests.Unit;

/// <summary>#353: <c>num_ctx</c> reaches Ollama, and every call's tokens and time are measured.</summary>
public sealed class LlmUsageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IList<ChatMessage> Prompt => [new(ChatRole.User, "synthetic prompt")];

    [Fact]
    public void Analysis_options_carry_num_ctx()
    {
        var options = AnalysisPromptBuilder.CreateOptions(16_384);

        options.AdditionalProperties.ShouldNotBeNull()[LlmCallMeter.NumCtxKey].ShouldBe(16_384);
    }

    [Fact]
    public async Task OllamaSharp_sends_num_ctx_in_the_request_options_and_reports_usage()
    {
        var handler = new CapturingHandler("""
            {"model":"test-chat","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"{}"},"done":true,"done_reason":"stop","prompt_eval_count":42,"eval_count":7}
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.example.com:11434") };
        using var ollama = new OllamaApiClient(http, "test-chat");

        var response = await ((IChatClient)ollama).GetResponseAsync(Prompt, AnalysisPromptBuilder.CreateOptions(12_288), Ct);

        handler.Body.ShouldNotBeNull().ShouldContain("\"num_ctx\":12288");
        response.Usage.ShouldNotBeNull();
        (response.Usage.InputTokenCount, response.Usage.OutputTokenCount).ShouldBe((42L, 7L));
    }

    [Fact]
    public async Task Meter_times_the_call_and_reads_its_usage()
    {
        var time = new FakeTimeProvider();
        var chat = new FakeChatClient { Usage = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 50 } };
        chat.Responder = _ =>
        {
            time.Advance(TimeSpan.FromMilliseconds(1500));
            return "{}";
        };

        var (response, usage) = await new LlmCallMeter(time, NullLogger.Instance)
            .GetResponseAsync(chat, Prompt, new ChatOptions(), "test-chat", 8192, 3, Ct);

        response.Text.ShouldBe("{}");
        usage.ShouldBe(new LlmUsage(1000, 50, 1500, 0));
    }

    [Fact]
    public async Task Missing_usage_counts_zero()
    {
        var (_, usage) = await new LlmCallMeter(new FakeTimeProvider(), NullLogger.Instance)
            .GetResponseAsync(new FakeChatClient(), Prompt, new ChatOptions(), null, 8192, 1, Ct);

        usage.ShouldBe(default);
    }

    [Theory]
    [InlineData(7372, 0)]
    [InlineData(7373, 1)]
    [InlineData(8192, 1)]
    public async Task Prompt_at_ninety_percent_of_num_ctx_counts_as_near_the_limit(long promptTokens, int near)
    {
        var chat = new FakeChatClient { Usage = new UsageDetails { InputTokenCount = promptTokens } };

        var (_, usage) = await new LlmCallMeter(new FakeTimeProvider(), NullLogger.Instance)
            .GetResponseAsync(chat, Prompt, new ChatOptions(), "test-chat", 8192, 1, Ct);

        usage.NearContextLimit.ShouldBe(near);
    }

    [Theory]
    [InlineData(2047, true)]
    [InlineData(2048, false)]
    [InlineData(131_072, false)]
    [InlineData(131_073, true)]
    public void Num_ctx_is_validated(int value, bool invalid)
    {
        var errors = SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, LlmNumCtx: value));

        errors.ContainsKey("llmNumCtx").ShouldBe(invalid);
    }

    [Fact]
    public void Num_ctx_defaults_to_8192()
    {
        new AppSettings().LlmNumCtx.ShouldBe(8192);
    }

    private sealed class CapturingHandler(string reply) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply.Trim() + "\n", Encoding.UTF8, "application/x-ndjson"),
            };
        }
    }
}
