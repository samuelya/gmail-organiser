using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit;

/// <summary>The Claude API chat client over the named client from <c>AddLlm</c> (with its retry handler) and a fake API.</summary>
public sealed class ClaudeApiChatTests : IDisposable
{
    private const string Model = "test-model-a";
    private const string ApiKey = "sk-test-0000-synthetic";

    private readonly FakeAnthropicHandler api = new();
    private ServiceProvider? services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => services?.Dispose();

    private IChatClient Create(int maxRetries = 3)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:ClaudeApiMaxRetries"] = maxRetries.ToString() })
            .Build();
        var collection = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLlm();
        collection.AddHttpClient(ClaudeApiHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => api);
        services = collection.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<LlmOptions>>().Value;
        return ClaudeApiChat.Create(services.GetRequiredService<IHttpClientFactory>(), options, ApiKey, Model);
    }

    private static List<ChatMessage> Messages() =>
        [new(ChatRole.System, "Synthetic system prompt"), new(ChatRole.User, "Synthetic email from shop@example.com")];

    [Fact]
    public async Task An_analysis_call_sends_prompt_schema_and_temperature_without_ollama_options()
    {
        api.Message("{}");
        using var chat = Create();

        await chat.GetResponseAsync(Messages(), AnalysisPromptBuilder.CreateOptions(numCtx: 8192), Ct);

        var request = api.Requests.ShouldHaveSingleItem();
        request.Path.ShouldBe("/v1/messages");
        request.Headers["x-api-key"].ShouldBe(ApiKey);
        var body = request.Body;
        body.GetProperty("model").GetString().ShouldBe(Model);
        body.GetProperty("max_tokens").GetInt32().ShouldBe(new LlmOptions().ClaudeApiMaxOutputTokens);
        body.GetProperty("temperature").GetDouble().ShouldBe(0);
        body.GetProperty("system")[0].GetProperty("text").GetString().ShouldBe("Synthetic system prompt");
        body.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString().ShouldNotBeNull().ShouldContain("shop@example.com");
        body.TryGetProperty(OllamaRequestOptions.ThinkKey, out _).ShouldBeFalse();
        body.TryGetProperty(LlmCallMeter.NumCtxKey, out _).ShouldBeFalse();
        body.GetRawText().ShouldNotContain("num_ctx");
        var schema = body.GetProperty("output_config").GetProperty("format");
        schema.GetProperty("type").GetString().ShouldBe("json_schema");
        var suggestion = schema.GetProperty("schema").GetProperty("properties").GetProperty("suggestions").GetProperty("items");
        suggestion.GetProperty("properties").TryGetProperty("topicLabel", out _).ShouldBeTrue();
        suggestion.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        schema.GetProperty("schema").GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task The_answer_text_parses_and_usage_comes_from_anthropic()
    {
        const string answer = """{"suggestions":[{"id":"m1","topicLabel":"Topic/Sub","isNewLabel":false,"mailType":"receipt","needsAction":false,"toBeDeleted":false,"unsubscribeSuggested":false,"confidence":0.9,"reason":"Synthetic reason"}]}""";
        api.Message(answer, inputTokens: 1234, outputTokens: 56);
        using var chat = Create();

        var response = await chat.GetResponseAsync(Messages(), AnalysisPromptBuilder.CreateOptions(numCtx: 8192), Ct);

        var parsed = SuggestionOutputParser.Parse(response.Text, new HashSet<string> { "m1" });
        parsed.Errors.ShouldBeEmpty();
        parsed.Valid.ShouldHaveSingleItem().Id.ShouldBe("m1");
        response.Usage.ShouldNotBeNull().InputTokenCount.ShouldBe(1234);
        response.Usage.OutputTokenCount.ShouldBe(56);
    }

    [Fact]
    public async Task An_explicit_max_output_tokens_wins_and_temperature_stays_within_one()
    {
        api.Message("ok");
        using var chat = Create();

        await chat.GetResponseAsync(Messages(), new ChatOptions { MaxOutputTokens = 64, Temperature = 1.5f }, Ct);

        var body = api.Requests.ShouldHaveSingleItem().Body;
        body.GetProperty("max_tokens").GetInt32().ShouldBe(64);
        body.GetProperty("temperature").GetDouble().ShouldBe(1);
    }

    [Fact]
    public async Task Rate_limit_and_overloaded_answers_are_retried_then_succeed()
    {
        api.Error(HttpStatusCode.TooManyRequests, "rate_limit_error", ("retry-after", "0"))
            .Error((HttpStatusCode)529, "overloaded_error", ("retry-after", "0"))
            .Message("ok");
        using var chat = Create();

        var response = await chat.GetResponseAsync(Messages(), cancellationToken: Ct);

        response.Text.ShouldBe("ok");
        api.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Overloaded_after_the_last_retry_surfaces_as_an_http_error()
    {
        api.Error((HttpStatusCode)529, "overloaded_error", ("retry-after", "0"));
        using var chat = Create(maxRetries: 2);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.StatusCode.ShouldBe((HttpStatusCode)529);
        ex.Message.ShouldContain("overloaded");
        api.Requests.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_error")]
    [InlineData(HttpStatusCode.Forbidden, "permission_error")]
    public async Task A_rejected_key_is_not_retried_and_never_echoed(HttpStatusCode status, string type)
    {
        api.Error(status, type);
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.StatusCode.ShouldBe(status);
        ex.Message.ShouldContain("rejected the API key");
        ex.ToString().ShouldNotContain(ApiKey);
        ex.ToString().ShouldNotContain("x-api-key", Case.Insensitive);
        api.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Disposing_the_client_keeps_the_pooled_handler()
    {
        api.Message("ok");
        var chat = Create();
        await chat.GetResponseAsync(Messages(), cancellationToken: Ct);

        chat.Dispose();

        api.Disposed.ShouldBeFalse();
    }

    [Fact]
    public void Retry_delay_follows_retry_after_else_backs_off_within_the_cap()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        ClaudeApiRetryHandler.RetryDelay(new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)), 0, now).ShouldBe(TimeSpan.FromSeconds(7));
        ClaudeApiRetryHandler.RetryDelay(new RetryConditionHeaderValue(now.AddSeconds(5)), 0, now).ShouldBe(TimeSpan.FromSeconds(5));
        ClaudeApiRetryHandler.RetryDelay(new RetryConditionHeaderValue(now.AddSeconds(-5)), 0, now).ShouldBe(TimeSpan.Zero);
        ClaudeApiRetryHandler.RetryDelay(new RetryConditionHeaderValue(TimeSpan.FromHours(1)), 0, now).ShouldBe(ClaudeApiRetryHandler.MaxWait);
        ClaudeApiRetryHandler.RetryDelay(null, 0, now).ShouldBe(TimeSpan.FromSeconds(1));
        ClaudeApiRetryHandler.RetryDelay(null, 2, now).ShouldBe(TimeSpan.FromSeconds(4));
        ClaudeApiRetryHandler.RetryDelay(null, 9, now).ShouldBe(ClaudeApiRetryHandler.MaxWait);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData((HttpStatusCode)529, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void Only_rate_limits_and_server_errors_are_retryable(HttpStatusCode status, bool expected) =>
        ClaudeApiRetryHandler.IsRetryable(status).ShouldBe(expected);

    [Fact]
    public void Prepare_options_leaves_a_call_without_ollama_fields_or_schema_alone()
    {
        var options = new ChatOptions { Temperature = 0, ResponseFormat = ChatResponseFormat.Text };

        ClaudeApiChat.PrepareOptions(options);

        options.Temperature.ShouldBe(0);
        options.AdditionalProperties.ShouldBeNull();
        options.ResponseFormat.ShouldBe(ChatResponseFormat.Text);
    }

    [Fact]
    public void Prepare_options_closes_nested_objects_and_keeps_the_schema_name()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"a":{"type":"object","properties":{"b":{"type":"string"}}}}}""").RootElement;
        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "synthetic") };

        ClaudeApiChat.PrepareOptions(options);

        var json = options.ResponseFormat.ShouldBeOfType<ChatResponseFormatJson>();
        json.SchemaName.ShouldBe("synthetic");
        var closed = json.Schema.ShouldNotBeNull();
        closed.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        closed.GetProperty("properties").GetProperty("a").GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
    }
}
