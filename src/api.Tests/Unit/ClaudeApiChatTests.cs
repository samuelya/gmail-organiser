using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Anthropic.Core;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Unit;

/// <summary>The Claude API chat client over the named client from <c>AddLlm</c> (with its retry handler) and a fake API.</summary>
public sealed class ClaudeApiChatTests : IDisposable
{
    private const string Model = "test-model-a";
    private const string ApiKey = "sk-test-0000-synthetic";

    private readonly FakeAnthropicHandler api = new();
    private readonly List<ServiceProvider> providers = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var provider in providers)
        {
            provider.Dispose();
        }
    }

    private IChatClient Create(int maxRetries = 3, Dictionary<string, string?>? settings = null, TimeProvider? time = null, HttpMessageHandler? handler = null)
    {
        var provider = BuildServices(maxRetries, settings, time, handler);
        var options = provider.GetRequiredService<IOptions<LlmOptions>>().Value;
        return ClaudeApiChat.Create(provider.GetRequiredService<IHttpClientFactory>(), options, ApiKey, Model);
    }

    /// <summary>The model test's client, as <see cref="LlmClientFactory"/> builds it, over its own services.</summary>
    private IChatClient CreateTest(Dictionary<string, string?>? settings = null, HttpMessageHandler? handler = null)
    {
        var provider = BuildServices(3, settings, null, handler);
        var options = provider.GetRequiredService<IOptions<LlmOptions>>().Value;
        return ClaudeApiChat.Create(
            provider.GetRequiredService<IHttpClientFactory>(), ClaudeApiHttp.NoRetryClientName, options, ApiKey, Model, options.ClaudeApiTestTimeout);
    }

    private ServiceProvider BuildServices(int maxRetries, Dictionary<string, string?>? settings, TimeProvider? time, HttpMessageHandler? handler)
    {
        settings ??= [];
        settings["Llm:ClaudeApiMaxRetries"] = maxRetries.ToString(CultureInfo.InvariantCulture);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var collection = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLlm();
        if (time is not null)
        {
            collection.AddSingleton(time);
        }

        collection.AddHttpClient(ClaudeApiHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => handler ?? api);
        collection.AddHttpClient(ClaudeApiHttp.NoRetryClientName).ConfigurePrimaryHttpMessageHandler(() => handler ?? api);
        var provider = collection.BuildServiceProvider();
        providers.Add(provider);
        return provider;
    }

    private static List<ChatMessage> Messages() =>
        [new(ChatRole.System, "Synthetic system prompt"), new(ChatRole.User, "Synthetic email from shop@example.com")];

    [Fact]
    public async Task An_analysis_call_sends_prompt_and_schema_without_ollama_options_or_temperature()
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
        body.TryGetProperty("temperature", out _).ShouldBeFalse();
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
    public async Task An_explicit_max_output_tokens_wins_and_sampling_parameters_are_never_sent()
    {
        api.Message("ok");
        using var chat = Create();

        await chat.GetResponseAsync(Messages(), new ChatOptions { MaxOutputTokens = 64, Temperature = 0.2f, TopP = 0.9f, TopK = 40 }, Ct);

        var body = api.Requests.ShouldHaveSingleItem().Body;
        body.GetProperty("max_tokens").GetInt32().ShouldBe(64);
        body.TryGetProperty("temperature", out _).ShouldBeFalse();
        body.TryGetProperty("top_p", out _).ShouldBeFalse();
        body.TryGetProperty("top_k", out _).ShouldBeFalse();
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
    public async Task A_bad_request_shows_anthropics_error_message_and_is_not_retried()
    {
        api.Error(HttpStatusCode.BadRequest, "invalid_request_error");
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ex.Message.ShouldContain("HTTP 400");
        ex.Message.ShouldContain("synthetic error");
        ex.ToString().ShouldNotContain(ApiKey);
        api.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not_found_error")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "request_too_large")]
    public async Task Any_other_client_error_shows_anthropics_error_message(HttpStatusCode status, string type)
    {
        api.Error(status, type);
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldStartWith($"The Claude API answered HTTP {(int)status}");
        ex.Message.ShouldEndWith(": synthetic error.");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"error":{"type":"invalid_request_error"}}""")]
    [InlineData("""{"error":{"type":"invalid_request_error","message":" \r\n "}}""")]
    public async Task A_client_error_without_a_message_keeps_the_short_text(string body)
    {
        api.ErrorBody(HttpStatusCode.BadRequest, body);
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldStartWith("The Claude API answered HTTP 400");
        ex.Message.ShouldEndWith(".");
        ex.Message.ShouldNotContain(":");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_error")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit_error")]
    public async Task Key_and_rate_limit_errors_never_include_the_body(HttpStatusCode status, string type)
    {
        api.Error(status, type, ("retry-after", "0"));
        using var chat = Create(maxRetries: 0);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldNotContain("synthetic error");
    }

    [Fact]
    public async Task An_error_message_is_kept_on_one_line()
    {
        api.ErrorBody(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { type = "error", error = new { type = "invalid_request_error", message = "line one\r\nINFO forged\tline\u0000 two." } }));
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldEndWith("): line one INFO forged line two.");
    }

    [Fact]
    public async Task A_long_error_message_is_truncated_without_splitting_a_surrogate_pair()
    {
        // 199 letters then an emoji: a cut at 200 chars would land between its two halves.
        api.ErrorBody(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { type = "error", error = new { type = "invalid_request_error", message = new string('x', 199) + "\U0001F600" + new string('y', 50) } }));
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldEndWith(new string('x', 199) + "….");
    }

    [Fact]
    public async Task Anthropic_environment_variables_never_reach_the_request()
    {
        string[] names = ["ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY"];
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://proxy.example.com");
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "synthetic-env-token");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-test-env-synthetic");
            api.Message("ok");
            using var chat = Create();

            await chat.GetResponseAsync(Messages(), cancellationToken: Ct);
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        var request = api.Requests.ShouldHaveSingleItem();
        request.Host.ShouldBe(new Uri(EnvironmentUrl.Production).Host);
        request.Headers["x-api-key"].ShouldBe(ApiKey);
        request.Headers.ContainsKey("authorization").ShouldBeFalse();
    }

    [Fact]
    public async Task The_configured_base_url_is_used()
    {
        api.Message("ok");
        using var chat = Create(settings: new() { ["Llm:ClaudeApiBaseUrl"] = "https://claude.example.com" });

        await chat.GetResponseAsync(Messages(), cancellationToken: Ct);

        api.Requests.ShouldHaveSingleItem().Host.ShouldBe("claude.example.com");
    }

    [Fact]
    public async Task The_header_filter_drops_headers_the_sdk_does_not_set()
    {
        api.Message("ok");
        using var invoker = new HttpMessageInvoker(new ClaudeApiHeaderFilter { InnerHandler = api });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://claude.example.com/v1/messages");
        request.Headers.Add("x-api-key", ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("X-Stainless-Lang", "csharp");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-env-token");
        request.Headers.Add("X-Custom-Gateway", "synthetic");

        using var _ = await invoker.SendAsync(request, Ct);

        api.Requests.ShouldHaveSingleItem().Headers.Keys.ShouldBe(["x-api-key", "anthropic-version", "x-stainless-lang"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_retry_wait_past_the_call_timeout_returns_the_rate_limit_at_once()
    {
        api.Error(HttpStatusCode.TooManyRequests, "rate_limit_error", ("retry-after", "45"));
        using var chat = Create(settings: new() { ["Llm:ClaudeApiTimeoutSeconds"] = "30" }, time: new FakeTimeProvider());

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.Message.ShouldContain("rate limited or overloaded");
        api.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_model_test_client_sends_one_attempt_while_the_chat_client_still_retries()
    {
        api.Error(HttpStatusCode.TooManyRequests, "rate_limit_error", ("retry-after", "30"));
        using var chat = Create(time: new FakeTimeProvider());
        using var test = CreateTest();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => test.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.Message.ShouldBe("The Claude API is rate limited or overloaded (HTTP 429); try again later.");
        api.Requests.Count.ShouldBe(1);

        api.Error(HttpStatusCode.TooManyRequests, "rate_limit_error", ("retry-after", "0")).Message("ok");
        (await chat.GetResponseAsync(Messages(), cancellationToken: Ct)).Text.ShouldBe("ok");
        api.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task The_model_test_client_times_out_with_its_own_timeout()
    {
        using var test = CreateTest(new() { ["Llm:ClaudeApiTestTimeoutSeconds"] = "1" }, new HangingHandler());

        var ex = await Should.ThrowAsync<HttpRequestException>(() => test.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldBe("No answer from the Claude API within 1 s.");
    }

    [Fact]
    public async Task An_unreadable_answer_surfaces_as_a_key_free_http_error()
    {
        api.Unreadable();
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldBe("The Claude API sent an answer this app could not read.");
        ex.ToString().ShouldNotContain(ApiKey);
    }

    [Fact]
    public async Task The_sdk_timeout_surfaces_as_a_claude_http_error()
    {
        using var chat = Create(settings: new() { ["Llm:ClaudeApiTimeoutSeconds"] = "1" }, handler: new HangingHandler());

        var ex = await Should.ThrowAsync<HttpRequestException>(() => chat.GetResponseAsync(Messages(), cancellationToken: Ct));

        ex.Message.ShouldBe("No answer from the Claude API within 1 s.");
    }

    [Fact]
    public async Task Streaming_errors_are_mapped_like_non_streaming_ones()
    {
        api.Error(HttpStatusCode.Unauthorized, "authentication_error");
        using var chat = Create();

        var ex = await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in chat.GetStreamingResponseAsync(Messages(), cancellationToken: Ct))
            {
            }
        });

        ex.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        ex.Message.ShouldContain("rejected the API key");
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
    public void Prepare_options_drops_sampling_and_leaves_a_call_without_ollama_fields_or_schema_alone()
    {
        var options = new ChatOptions { Temperature = 0, TopP = 0.9f, TopK = 40, ResponseFormat = ChatResponseFormat.Text };

        ClaudeApiChat.PrepareOptions(options);

        options.Temperature.ShouldBeNull();
        options.TopP.ShouldBeNull();
        options.TopK.ShouldBeNull();
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

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
