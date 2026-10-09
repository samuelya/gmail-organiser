using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Llm;

/// <summary>
/// HTTP timeouts for Ollama and the Claude API, bound from the <c>Llm</c> configuration section; <c>LLM_FAKE</c> (from <c>.env</c>)
/// overrides <see cref="UseFake"/>.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";
    public const string FakeEnvironmentKey = "LLM_FAKE";

    /// <summary>Run the whole app against the deterministic fakes in <c>Llm/Fake</c>, without Ollama.</summary>
    public bool UseFake { get; set; }

    /// <summary>For <c>/api/tags</c>, <c>/api/show</c> and <c>/api/version</c>.</summary>
    public int CatalogTimeoutSeconds { get; set; } = 10;

    /// <summary>For chat, embedding and vision calls, including model load time; validated at startup.</summary>
    [Range(1, 3600, ErrorMessage = "Llm:ModelTimeoutSeconds (Llm__ModelTimeoutSeconds) must be a whole number of seconds from 1 to 3600.")]
    public int ModelTimeoutSeconds { get; set; } = 180;

    /// <summary>Bounds one Claude API chat call, including its retries.</summary>
    [Range(1, 3600, ErrorMessage = "Llm:ClaudeApiTimeoutSeconds (Llm__ClaudeApiTimeoutSeconds) must be a whole number of seconds from 1 to 3600.")]
    public int ClaudeApiTimeoutSeconds { get; set; } = 120;

    /// <summary>Bounds one Settings model test (one attempt, no retries); never more than <see cref="ClaudeApiTimeoutSeconds"/>.</summary>
    [Range(1, 3600, ErrorMessage = "Llm:ClaudeApiTestTimeoutSeconds (Llm__ClaudeApiTestTimeoutSeconds) must be a whole number of seconds from 1 to 3600.")]
    public int ClaudeApiTestTimeoutSeconds { get; set; } = 15;

    /// <summary>Retries of a Claude API call answered 429, 529 or another 5xx.</summary>
    [Range(0, 10, ErrorMessage = "Llm:ClaudeApiMaxRetries (Llm__ClaudeApiMaxRetries) must be a whole number from 0 to 10.")]
    public int ClaudeApiMaxRetries { get; set; } = 3;

    /// <summary>
    /// Anthropic's required <c>max_tokens</c>, sent when the call sets no <see cref="ChatOptions.MaxOutputTokens"/>. At most
    /// <see cref="MaxNonStreamingOutputTokens"/>: calls are non-streaming, and the SDK refuses more for some models.
    /// </summary>
    [Range(1, MaxNonStreamingOutputTokens, ErrorMessage = "Llm:ClaudeApiMaxOutputTokens (Llm__ClaudeApiMaxOutputTokens) must be a whole number from 1 to 8192.")]
    public int ClaudeApiMaxOutputTokens { get; set; } = 8192;

    /// <summary>The SDK's lowest non-streaming <c>max_tokens</c> limit (<c>ClientOptions.TimeoutFromMaxTokens</c>).</summary>
    public const int MaxNonStreamingOutputTokens = 8192;

    /// <summary>Claude API base URL; empty means the SDK's production URL. Never read from <c>ANTHROPIC_BASE_URL</c>.</summary>
    [Url(ErrorMessage = "Llm:ClaudeApiBaseUrl (Llm__ClaudeApiBaseUrl) must be an http or https URL.")]
    public string? ClaudeApiBaseUrl { get; set; }

    public TimeSpan CatalogTimeout => TimeSpan.FromSeconds(Math.Max(1, CatalogTimeoutSeconds));
    public TimeSpan ModelTimeout => TimeSpan.FromSeconds(ModelTimeoutSeconds);
    public TimeSpan ClaudeApiTimeout => TimeSpan.FromSeconds(ClaudeApiTimeoutSeconds);

    /// <summary>The model test is never looser than a real call.</summary>
    public TimeSpan ClaudeApiTestTimeout => TimeSpan.FromSeconds(Math.Min(ClaudeApiTestTimeoutSeconds, ClaudeApiTimeoutSeconds));
}

/// <summary>Request fields every Ollama chat call carries, applied once in <see cref="LlmClientFactory"/>.</summary>
public static class OllamaRequestOptions
{
    /// <summary>
    /// The <see cref="ChatOptions.AdditionalProperties"/> key OllamaSharp maps to the request's top-level <c>think</c>
    /// field (not under <c>options</c>).
    /// </summary>
    public const string ThinkKey = "think";

    /// <summary>
    /// Sends <c>"think":false</c> unless the call set <see cref="ThinkKey"/> itself, so a thinking model answers without
    /// a long hidden reasoning phase (#457). Ollama rejects <c>think</c> only when it is <c>true</c> on a model without
    /// thinking, so this is safe for every model. The options are the per-call clone, never a caller's shared instance.
    /// </summary>
    public static void NoThink(ChatOptions options)
    {
        options.AdditionalProperties ??= [];
        options.AdditionalProperties.TryAdd(ThinkKey, false);
    }
}

/// <summary>Creates <see cref="HttpClient"/>s for the named <c>ollama</c> client.</summary>
public static class OllamaHttp
{
    public const string ClientName = "ollama";

    public static HttpClient Create(IHttpClientFactory factory, Uri baseUrl, TimeSpan timeout)
    {
        var client = factory.CreateClient(ClientName);
        client.BaseAddress = WithTrailingSlash(baseUrl);
        client.Timeout = timeout;
        return client;
    }

    /// <summary>Parses a URL validated by <c>SettingsValidation.IsHttpUrl</c>.</summary>
    public static Uri Parse(string baseUrl) => new(baseUrl.Trim(), UriKind.Absolute);

    // Relative API paths resolve under a base path (e.g. a reverse proxy prefix) only with a trailing slash.
    private static Uri WithTrailingSlash(Uri uri) =>
        uri.AbsolutePath.EndsWith('/') ? uri : new UriBuilder(uri) { Path = uri.AbsolutePath + "/" }.Uri;
}
