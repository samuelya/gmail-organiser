using System.Net;
using System.Net.Http.Headers;
using Anthropic;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Llm.ClaudeApi;

/// <summary>
/// Builds the <see cref="IChatClient"/> for <see cref="LlmProvider.ClaudeApi"/> (#487): the official SDK's adapter over the
/// named <c>claude-api</c> client, so every chat caller works unchanged. Retries live in <see cref="ClaudeApiRetryHandler"/>
/// on the named client (the SDK's own backoff can't run on a <see cref="TimeProvider"/>), so the SDK's are off.
/// </summary>
public static class ClaudeApiChat
{
    /// <summary>The client disposes its <see cref="HttpClient"/>, never the pooled handler behind it.</summary>
    public static IChatClient Create(IHttpClientFactory httpClients, LlmOptions options, string apiKey, string model)
    {
        var http = httpClients.CreateClient(ClaudeApiHttp.ClientName);
        http.Timeout = options.ClaudeApiTimeout;
        var anthropic = new AnthropicClient { ApiKey = apiKey, HttpClient = http, MaxRetries = 0, Timeout = options.ClaudeApiTimeout };
        return new ChatClientBuilder(anthropic.AsIChatClient(model, options.ClaudeApiMaxOutputTokens))
            .ConfigureOptions(PrepareOptions)
            .Use(inner => new ClaudeApiErrorClient(inner, anthropic))
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
            var closed = AIJsonUtilities.TransformSchema(schema, new AIJsonSchemaTransformOptions { DisallowAdditionalProperties = true });
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema(closed, json.SchemaName, json.SchemaDescription);
        }
    }
}

/// <summary>The named <c>claude-api</c> client; its timeout comes from <see cref="LlmOptions.ClaudeApiTimeoutSeconds"/>.</summary>
public static class ClaudeApiHttp
{
    public const string ClientName = "claude-api";
}

/// <summary>
/// Retries 429, 529 and other 5xx answers up to <see cref="LlmOptions.ClaudeApiMaxRetries"/> times, waiting
/// <c>retry-after</c> or an exponential backoff (both capped at <see cref="MaxWait"/>); the last answer is returned as is.
/// </summary>
public sealed class ClaudeApiRetryHandler(int maxRetries, TimeProvider time) : DelegatingHandler
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (attempt >= maxRetries || !IsRetryable(response.StatusCode))
            {
                return response;
            }

            var delay = RetryDelay(response.Headers.RetryAfter, attempt, time.GetUtcNow());
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
/// Rethrows the SDK's errors as <see cref="HttpRequestException"/>s with the status code, so callers treat them like an
/// unreachable Ollama; messages carry the status and Anthropic's error type, never the key or a header. Owns the
/// <see cref="AnthropicClient"/>.
/// </summary>
internal sealed class ClaudeApiErrorClient(IChatClient inner, AnthropicClient anthropic) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (AnthropicApiException ex)
        {
            throw new HttpRequestException(Describe(ex), null, ex.StatusCode);
        }
        catch (AnthropicIOException ex)
        {
            throw new HttpRequestException("Could not reach the Claude API.", ex.InnerException);
        }
    }

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
