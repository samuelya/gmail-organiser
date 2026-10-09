using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Llm.ClaudeApi;

/// <summary>
/// Builds the <see cref="IChatClient"/> for <see cref="LlmProvider.ClaudeApi"/> (#487): the official SDK's adapter over the
/// named <c>claude-api</c> client, so every chat caller works unchanged. Retries live in <see cref="ClaudeApiRetryHandler"/>
/// on the named client (the SDK's own backoff can't run on a <see cref="TimeProvider"/>), so the SDK's are off. The SDK's
/// <see cref="ClientOptions.Timeout"/> is the only timeout and bounds one call including those retries.
/// </summary>
public static class ClaudeApiChat
{
    private static readonly ConcurrentDictionary<string, JsonElement> ClosedSchemas = new();

    /// <summary>
    /// The client disposes its <see cref="HttpClient"/>, never the pooled handler behind it. Base URL, key and auth token
    /// are set explicitly, so no <c>ANTHROPIC_*</c> environment variable or profile reaches the client.
    /// </summary>
    public static IChatClient Create(IHttpClientFactory httpClients, LlmOptions options, string apiKey, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var http = httpClients.CreateClient(ClaudeApiHttp.ClientName);
        http.Timeout = Timeout.InfiniteTimeSpan;
        var anthropic = new AnthropicClient
        {
            BaseUrl = options.ClaudeApiBaseUrl ?? EnvironmentUrl.Production,
            ApiKey = apiKey,
            AuthToken = null,
            HttpClient = http,
            MaxRetries = 0,
            Timeout = options.ClaudeApiTimeout,
        };
        return new ChatClientBuilder(anthropic.AsIChatClient(model, options.ClaudeApiMaxOutputTokens))
            .ConfigureOptions(PrepareOptions)
            .Use(inner => new ClaudeApiErrorClient(inner, anthropic, options.ClaudeApiTimeout))
            .Build();
    }

    /// <summary>
    /// Removes the Ollama-only <c>think</c> and <c>num_ctx</c> fields, keeps temperature in Anthropic's 0–1 range, and
    /// closes every object in a JSON schema (<c>additionalProperties: false</c>), which Anthropic's structured outputs
    /// require. The options are the per-call clone, never a caller's shared instance.
    /// </summary>
    public static void PrepareOptions(ChatOptions options)
    {
        options.AdditionalProperties?.Remove(OllamaRequestOptions.ThinkKey);
        options.AdditionalProperties?.Remove(LlmCallMeter.NumCtxKey);
        if (options.Temperature > 1)
        {
            options.Temperature = 1;
        }

        if (options.ResponseFormat is ChatResponseFormatJson { Schema: { } schema } json)
        {
            // The callers' schemas are static, so each is transformed once.
            var closed = ClosedSchemas.GetOrAdd(schema.GetRawText(), static (_, schema) =>
                AIJsonUtilities.TransformSchema(schema, new AIJsonSchemaTransformOptions { DisallowAdditionalProperties = true }), schema);
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema(closed, json.SchemaName, json.SchemaDescription);
        }
    }
}

/// <summary>The named <c>claude-api</c> client; the SDK's timeout bounds its calls, so its own is infinite.</summary>
public static class ClaudeApiHttp
{
    public const string ClientName = "claude-api";
}

/// <summary>
/// Drops every request header the SDK did not set for the API itself, i.e. what <c>ANTHROPIC_CUSTOM_HEADERS</c> adds
/// (the SDK reads it once per process with no option to turn it off), including any <c>Authorization</c>.
/// </summary>
public sealed class ClaudeApiHeaderFilter : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        foreach (var name in request.Headers.Select(h => h.Key).Where(name => !IsAllowed(name)).ToList())
        {
            request.Headers.Remove(name);
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>The key, the SDK's <c>anthropic-*</c> and <c>x-stainless-*</c> headers, and the generic HTTP ones.</summary>
    public static bool IsAllowed(string name) =>
        name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
        || name.Equals("user-agent", StringComparison.OrdinalIgnoreCase)
        || name.Equals("accept", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("anthropic-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("x-stainless-", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Retries 429, 529 and other 5xx answers up to <see cref="LlmOptions.ClaudeApiMaxRetries"/> times, waiting
/// <c>retry-after</c> or an exponential backoff (both capped at <see cref="MaxWait"/>); the last answer is returned as is,
/// also when the wait would outlast <paramref name="budget"/> (the call's timeout), so the caller sees the 429/529.
/// </summary>
public sealed class ClaudeApiRetryHandler(int maxRetries, TimeSpan budget, TimeProvider time) : DelegatingHandler
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + budget;
        for (var attempt = 0; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (attempt >= maxRetries || !IsRetryable(response.StatusCode))
            {
                return response;
            }

            var now = time.GetUtcNow();
            var delay = RetryDelay(response.Headers.RetryAfter, attempt, now);
            if (now + delay >= deadline)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(delay, time, cancellationToken);
        }
    }

    /// <summary>429 (rate limit), 529 (overloaded) and the other 5xx answers.</summary>
    public static bool IsRetryable(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary><c>retry-after</c> (seconds or a date) when present, else 1, 2, 4… seconds; within 0 and <see cref="MaxWait"/>.</summary>
    public static TimeSpan RetryDelay(RetryConditionHeaderValue? retryAfter, int attempt, DateTimeOffset now)
    {
        var wait = retryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - now,
            _ => TimeSpan.FromSeconds(Math.Pow(2, Math.Min(attempt, 10))),
        };
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxWait ? MaxWait : wait;
    }
}

/// <summary>
/// Rethrows every SDK error, and the SDK's timeout, as an <see cref="HttpRequestException"/> (with the status code when
/// there is one), so callers treat it like an unreachable Ollama; messages carry the status and Anthropic's error type,
/// never the key, a header or the SDK's own text. Owns the <see cref="AnthropicClient"/>.
/// </summary>
internal sealed class ClaudeApiErrorClient(IChatClient inner, AnthropicClient anthropic, TimeSpan timeout) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (Map(ex, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var updates = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try
            {
                if (!await updates.MoveNextAsync())
                {
                    yield break;
                }
            }
            catch (Exception ex) when (Map(ex, cancellationToken) is { } mapped)
            {
                throw mapped;
            }

            yield return updates.Current;
        }
    }

    /// <summary>The key-free replacement for an SDK error or timeout; <c>null</c> for anything else (e.g. the caller's cancel).</summary>
    private HttpRequestException? Map(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        AnthropicApiException api => new HttpRequestException(Describe(api), null, api.StatusCode),
        AnthropicIOException io => new HttpRequestException("Could not reach the Claude API.", io.InnerException),
        AnthropicException => new HttpRequestException("The Claude API sent an answer this app could not read."),
        TaskCanceledException { InnerException: TimeoutException } when !cancellationToken.IsCancellationRequested =>
            new HttpRequestException($"No answer from the Claude API within {timeout.TotalSeconds:0} s."),
        _ => null,
    };

    public static string Describe(AnthropicApiException ex)
    {
        var code = (int)ex.StatusCode;
        return ex.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"The Claude API rejected the API key (HTTP {code}). Check the key in Settings.",
            HttpStatusCode.TooManyRequests or (HttpStatusCode)529 =>
                $"The Claude API is rate limited or overloaded (HTTP {code}); try again later.",
            _ => $"The Claude API answered HTTP {code}" + (ex.ErrorType is { } type ? $" ({type})." : "."),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            anthropic.Dispose();
        }

        base.Dispose(disposing);
    }
}
