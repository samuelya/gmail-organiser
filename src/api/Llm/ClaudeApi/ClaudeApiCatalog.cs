using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic.Core;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Llm.ClaudeApi;

/// <summary>Lists the Claude API models the saved key can use. Never cached: the key can change at any time.</summary>
public interface IClaudeApiCatalog
{
    /// <summary>No key: <see cref="ClaudeApiModelsDto.NoKey"/> without a call. An API or network error is a DTO, not an exception.</summary>
    Task<ClaudeApiModelsDto> ListModelsAsync(CancellationToken ct = default);
}

/// <summary>
/// Pages through <c>GET /v1/models</c> over the named <c>claude-api</c> client (its header filter and retry handler apply),
/// at most <see cref="MaxPages"/> pages of <see cref="PageSize"/>, within <see cref="LlmOptions.CatalogTimeout"/>.
/// Errors carry the status or the exception type only, never the key, a header or Anthropic's text.
/// </summary>
public sealed class ClaudeApiCatalog(
    IHttpClientFactory httpClients,
    ClaudeApiKeyService keys,
    IOptions<LlmOptions> options,
    ILogger<ClaudeApiCatalog> logger) : IClaudeApiCatalog
{
    public const int PageSize = 100;
    public const int MaxPages = 10;
    public const string ApiVersion = "2023-06-01";
    public const string RejectedKeyError = "The Claude API rejected the key.";

    public async Task<ClaudeApiModelsDto> ListModelsAsync(CancellationToken ct = default)
    {
        var apiKey = await keys.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ClaudeApiModelsDto.NoKey;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.CatalogTimeout);
        try
        {
            return ClaudeApiModelsDto.Listed(await ListAllAsync(apiKey, timeout.Token));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && Describe(ex) is { } error)
        {
            logger.LogInformation("Claude API model list failed ({Error})", ex.GetType().Name);
            return ClaudeApiModelsDto.Unreachable(Truncate(error));
        }
    }

    private async Task<List<ClaudeApiModelDto>> ListAllAsync(string apiKey, CancellationToken ct)
    {
        using var http = httpClients.CreateClient(ClaudeApiHttp.ClientName);
        http.Timeout = Timeout.InfiniteTimeSpan;
        var baseUrl = BaseUrl(options.Value.ClaudeApiBaseUrl);
        var models = new List<ClaudeApiModelDto>();
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var query = $"v1/models?limit={PageSize}" + (afterId is null ? "" : $"&after_id={Uri.EscapeDataString(afterId)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, query));
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", ApiVersion);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ClaudeApiStatusException(response.StatusCode);
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var list = await JsonSerializer.DeserializeAsync<ModelPage>(body, cancellationToken: ct)
                ?? throw new JsonException("Empty model list.");
            models.AddRange((list.Data ?? [])
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => new ClaudeApiModelDto(m.Id!, string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id! : m.DisplayName, m.CreatedAt)));
            if (!list.HasMore || string.IsNullOrEmpty(list.LastId))
            {
                break;
            }

            afterId = list.LastId;
        }

        return models;
    }

    /// <summary>The readable error for a failed list; <c>null</c> for anything that is not an API or network failure.</summary>
    private static string? Describe(Exception ex) => ex switch
    {
        ClaudeApiStatusException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => RejectedKeyError,
        ClaudeApiStatusException status => $"The Claude API returned {(int)status.StatusCode}.",
        HttpRequestException or OperationCanceledException or IOException => $"Cannot reach the Claude API ({ex.GetType().Name}).",
        JsonException => "The Claude API sent a model list this app could not read.",
        _ => null,
    };

    private static string Truncate(string message) => message.Length <= 300 ? message : message[..300] + "…";

    // Relative API paths resolve under a base path only with a trailing slash.
    private static Uri BaseUrl(string? configured)
    {
        var uri = new Uri(string.IsNullOrWhiteSpace(configured) ? EnvironmentUrl.Production : configured.Trim(), UriKind.Absolute);
        return uri.AbsolutePath.EndsWith('/') ? uri : new UriBuilder(uri) { Path = uri.AbsolutePath + "/" }.Uri;
    }

    private sealed class ClaudeApiStatusException(HttpStatusCode statusCode) : Exception
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }

    private sealed record ModelPage(
        [property: JsonPropertyName("data")] List<ModelInfo>? Data,
        [property: JsonPropertyName("has_more")] bool HasMore,
        [property: JsonPropertyName("last_id")] string? LastId);

    private sealed record ModelInfo(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("display_name")] string? DisplayName,
        [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt);
}
