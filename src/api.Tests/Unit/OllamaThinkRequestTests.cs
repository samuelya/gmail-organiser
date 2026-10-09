using System.Text.Json;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Llm;
using GmailOrganiser.Policies.Prompts;
using GmailOrganiser.Rules.Prompts;
using GmailOrganiser.Rules.Taxonomy;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit;

/// <summary>The <c>/api/chat</c> body OllamaSharp sends for each structured call carries a top-level <c>"think":false</c> (#457).</summary>
public sealed class OllamaThinkRequestTests
{
    private const string Model = "test-chat:1b";
    private static readonly Uri BaseUrl = new("http://ollama.example.com:11434");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Builders => ["analysis", "senderPolicy", "taxonomy", "rulesSummary"];

    private static StubOllamaHandler ChatStub(string content) =>
        new StubOllamaHandler().Json("/api/chat", new
        {
            model = Model,
            message = new { role = "assistant", content },
            done = true,
        });

    private static LlmClientFactory Factory(StubOllamaHandler stub)
    {
        var settings = new InMemorySettingsStore();
        return new(new StubHttpClientFactory(stub), settings, TestClaudeApiKeys.For(settings), Options.Create(new LlmOptions()));
    }

    private static async Task<JsonElement> SendAsync(ChatOptions options)
    {
        var stub = ChatStub("{}");
        using var client = Factory(stub).CreateChatClient(BaseUrl, Model);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], options, Ct);
        return ChatBody(stub);
    }

    private static JsonElement ChatBody(StubOllamaHandler stub) =>
        JsonDocument.Parse(stub.Requests.Single(r => r.Uri.AbsolutePath == "/api/chat").Body!).RootElement.Clone();

    private static void ShouldNotThink(JsonElement body)
    {
        body.GetProperty("think").ValueKind.ShouldBe(JsonValueKind.False);
        if (body.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object)
        {
            options.TryGetProperty("think", out _).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Think_false_is_sent_top_level_next_to_num_ctx()
    {
        var body = await SendAsync(new ChatOptions { AdditionalProperties = new() { [LlmCallMeter.NumCtxKey] = 4096 } });

        ShouldNotThink(body);
        body.GetProperty("options").GetProperty("num_ctx").GetInt32().ShouldBe(4096);
    }

    [Fact]
    public async Task Think_false_is_sent_without_options()
    {
        var stub = ChatStub("{}");
        using var client = Factory(stub).CreateChatClient(BaseUrl, Model);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: Ct);

        ShouldNotThink(ChatBody(stub));
    }

    [Fact]
    public async Task The_callers_options_are_not_mutated()
    {
        var options = new ChatOptions { AdditionalProperties = new() { [LlmCallMeter.NumCtxKey] = 8192 } };

        await SendAsync(options);

        options.AdditionalProperties.ContainsKey(OllamaRequestOptions.ThinkKey).ShouldBeFalse();
        options.AdditionalProperties[LlmCallMeter.NumCtxKey].ShouldBe(8192);
    }

    [Fact]
    public async Task A_calls_own_think_value_is_kept()
    {
        var body = await SendAsync(new ChatOptions { AdditionalProperties = new() { [OllamaRequestOptions.ThinkKey] = true } });

        body.GetProperty("think").ValueKind.ShouldBe(JsonValueKind.True);
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public async Task Every_structured_prompt_builder_sends_think_false(string builder)
    {
        var options = builder switch
        {
            "analysis" => AnalysisPromptBuilder.CreateOptions(8192),
            "senderPolicy" => SenderPolicyPromptBuilder.CreateOptions(8192),
            "taxonomy" => TaxonomyPrompt.CreateOptions(8192),
            "rulesSummary" => RulesSummaryPromptBuilder.CreateOptions(),
            _ => throw new ArgumentOutOfRangeException(nameof(builder)),
        };

        ShouldNotThink(await SendAsync(options));
    }

    [Fact]
    public async Task The_model_test_sends_think_false()
    {
        var stub = ChatStub("""{"ok":true}""");
        var tester = new LlmModelTester(Factory(stub), Options.Create(new LlmOptions()), TimeProvider.System,
            NullLogger<LlmModelTester>.Instance);

        var result = await tester.TestAsync(ModelKinds.Chat, Model, BaseUrl, Ct);

        result.Ok.ShouldBeTrue(result.Error);
        ShouldNotThink(ChatBody(stub));
    }

    [Fact]
    public async Task The_vision_client_sends_think_false()
    {
        var stub = ChatStub("""{"description":"A synthetic test image","text":""}""");
        var vision = new OllamaVisionClient(Factory(stub));
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

        var text = await vision.ReadAsync(png, new ImageReading(ImageMode.Vision, Model, BaseUrl.ToString()), Ct);

        text.ShouldNotBeNull();
        ShouldNotThink(ChatBody(stub));
    }
}
