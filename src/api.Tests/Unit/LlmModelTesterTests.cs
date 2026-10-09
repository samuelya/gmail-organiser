using System.Net.Sockets;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Unit;

public sealed class LlmModelTesterTests
{
    private static readonly Uri BaseUrl = new("http://ollama.example.com:11434");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeChatClient _chat = new();
    private readonly FakeEmbeddingGenerator _embed = new(dimension: 4);

    // Built, not a literal: a key-shaped literal trips the secret scan.
    private static readonly string SyntheticKey = new string('k', 30) + "WXYZ";

    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeLlmClientFactory _factory;

    public LlmModelTesterTests() => _factory = new FakeLlmClientFactory(_chat, _embed);

    private LlmModelTester Tester() => new(
        _factory,
        TestClaudeApiKeys.For(_settings),
        Options.Create(new LlmOptions()),
        new FakeTimeProvider(),
        NullLogger<LlmModelTester>.Instance);

    [Fact]
    public async Task Chat_success_sends_one_json_mode_request()
    {
        var result = await Tester().TestAsync(ModelKinds.Chat, "test-chat:1b", BaseUrl, Ct);

        result.Ok.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.ElapsedMs.ShouldBeGreaterThanOrEqualTo(0);
        var request = _chat.Requests.ShouldHaveSingleItem();
        request.Options!.ResponseFormat.ShouldBe(ChatResponseFormat.Json);
        request.Messages.Single().Text.ShouldContain("""{"ok":true}""");
        _chat.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Chat_reply_that_is_not_json_fails()
    {
        _chat.Enqueue("sure, here you go");

        var result = await Tester().TestAsync(ModelKinds.Chat, "test-chat:1b", BaseUrl, Ct);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Chat_connection_failure_is_a_readable_error()
    {
        _chat.Failure = new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused));

        var result = await Tester().TestAsync(ModelKinds.Chat, "test-chat:1b", BaseUrl, Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("Connection refused");
    }

    [Fact]
    public async Task Missing_model_keeps_the_server_message()
    {
        _chat.Failure = new HttpRequestException("model 'test-missing' not found", null, System.Net.HttpStatusCode.NotFound);

        var result = await Tester().TestAsync(ModelKinds.Chat, "test-missing", BaseUrl, Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("not found");
    }

    [Fact]
    public async Task Embedding_success_embeds_one_word()
    {
        var result = await Tester().TestAsync(ModelKinds.Embedding, "test-embed", BaseUrl, Ct);

        result.Ok.ShouldBeTrue();
        _embed.Inputs.ShouldBe([LlmModelTester.EmbeddingInput]);
        _embed.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Embedding_failure_is_reported()
    {
        _embed.Failure = new TaskCanceledException("timeout", new TimeoutException());

        var result = await Tester().TestAsync(ModelKinds.Embedding, "test-embed", BaseUrl, Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("No answer");
    }

    [Fact]
    public void Fake_embeddings_are_deterministic_unit_vectors()
    {
        var a = _embed.Vector("hello");

        a.ShouldBe(new FakeEmbeddingGenerator(4).Vector("hello"));
        a.ShouldNotBe(_embed.Vector("world"));
        MathF.Sqrt(a.Sum(x => x * x)).ShouldBe(1f, 1e-5f);
        new FakeEmbeddingGenerator(100).Vector("hello").Length.ShouldBe(100);
    }

    [Fact]
    public async Task Claude_api_success_sends_one_json_mode_request_with_the_saved_key()
    {
        await TestClaudeApiKeys.For(_settings).SetAsync(SyntheticKey, Ct);

        var result = await Tester().TestClaudeApiAsync("test-model-a", Ct);

        result.Ok.ShouldBeTrue();
        _factory.ClaudeApiTestModels.ShouldBe(["test-model-a"]);
        _chat.Requests.ShouldHaveSingleItem().Options!.ResponseFormat.ShouldBe(LlmModelTester.ClaudeApiFormat);
        _chat.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Claude_api_test_through_the_real_adapter_sends_a_closed_schema_and_passes()
    {
        var api = new FakeAnthropicHandler().Message("""{"ok":true}""");

        var result = await TestThroughTheRealAdapterAsync(api);

        result.Error.ShouldBeNull();
        result.Ok.ShouldBeTrue();
        var format = api.Requests.ShouldHaveSingleItem().Body.GetProperty("output_config").GetProperty("format");
        format.GetProperty("type").GetString().ShouldBe("json_schema");
        format.GetProperty("schema").GetProperty("properties").TryGetProperty("ok", out _).ShouldBeTrue();
        format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
    }

    [Theory]
    [InlineData(429, "rate_limit_error", "rate limited or overloaded (HTTP 429)")]
    [InlineData(529, "overloaded_error", "rate limited or overloaded (HTTP 529)")]
    [InlineData(500, "api_error", "answered HTTP 500 (ApiError)")]
    public async Task Claude_api_test_reports_a_rate_limit_or_server_error_after_one_attempt(int status, string type, string wording)
    {
        var api = new FakeAnthropicHandler().Error((System.Net.HttpStatusCode)status, type, ("retry-after", "30"));

        var result = await TestThroughTheRealAdapterAsync(api);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain(wording);
        result.Error.ShouldNotContain(SyntheticKey);
        api.Requests.Count.ShouldBe(1);
    }

    /// <summary>Runs the Claude API test through the real factory and adapter; only the chat client's named client is the fake API.</summary>
    private async Task<TestModelResultDto> TestThroughTheRealAdapterAsync(FakeAnthropicHandler api)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:ClaudeApiBaseUrl"] = "https://claude.example.com" })
            .Build();
        var collection = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLlm();
        collection.AddHttpClient(ClaudeApiHttp.NoRetryClientName).ConfigurePrimaryHttpMessageHandler(() => api);
        using var services = collection.BuildServiceProvider();
        var keys = TestClaudeApiKeys.For(_settings);
        await keys.SetAsync(SyntheticKey, Ct);
        var options = services.GetRequiredService<IOptions<LlmOptions>>();
        var factory = new LlmClientFactory(services.GetRequiredService<IHttpClientFactory>(), _settings, keys, options);
        var tester = new LlmModelTester(factory, keys, options, new FakeTimeProvider(), NullLogger<LlmModelTester>.Instance);
        return await tester.TestClaudeApiAsync("test-model-a", Ct);
    }

    [Fact]
    public async Task Claude_api_without_a_key_is_not_configured()
    {
        await Should.ThrowAsync<LlmNotConfiguredException>(() => Tester().TestClaudeApiAsync("test-model-a", Ct));

        _factory.ClaudeApiTestModels.ShouldBeEmpty();
    }

    [Fact]
    public async Task Claude_api_error_keeps_the_mapped_message_truncated()
    {
        await TestClaudeApiKeys.For(_settings).SetAsync(SyntheticKey, Ct);
        _chat.Failure = new HttpRequestException(new string('x', 400), null, System.Net.HttpStatusCode.NotFound);

        var result = await Tester().TestClaudeApiAsync("test-missing", Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.Length.ShouldBeLessThanOrEqualTo(330);
        result.Error.ShouldStartWith("The model call failed: xxx");
    }

    [Fact]
    public async Task Claude_api_other_failure_shows_only_its_type()
    {
        await TestClaudeApiKeys.For(_settings).SetAsync(SyntheticKey, Ct);
        _chat.Failure = new InvalidOperationException($"synthetic {SyntheticKey}");

        var result = await Tester().TestClaudeApiAsync("test-model-a", Ct);

        result.Error.ShouldBe("The model call failed (InvalidOperationException).");
    }
}
