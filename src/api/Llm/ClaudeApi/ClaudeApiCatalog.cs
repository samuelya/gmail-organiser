using Anthropic;
using Anthropic.Models.Models;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Llm.ClaudeApi;

/// <summary>Lists the Claude API models the saved key can use. Never cached: the key can change at any time.</summary>
public interface IClaudeApiCatalog
{
    /// <summary>No key: <see cref="ClaudeApiModelsDto.NoKey"/> without a call. An API or network error is a DTO, not an exception.</summary>
    Task<ClaudeApiModelsDto> ListModelsAsync(CancellationToken ct = default);
}

/// <summary>
/// Pages through the SDK's <c>Models.List</c> over the named <see cref="ClaudeApiHttp.CatalogClientName"/> client (header
/// filter, no retries), built like the chat client (<see cref="ClaudeApiChat.CreateAnthropic"/>): at most
/// <see cref="MaxPages"/> pages of <see cref="PageSize"/>, within <see cref="LlmOptions.CatalogTimeout"/>.
/// Errors use the chat client's wording (<see cref="ClaudeApiErrorClient.Map"/>), never the key, a header or Anthropic's text.
/// </summary>
public sealed class ClaudeApiCatalog(
    IHttpClientFactory httpClients,
    ClaudeApiKeyService keys,
    IOptions<LlmOptions> options,
    ILogger<ClaudeApiCatalog> logger) : IClaudeApiCatalog
{
    public const int PageSize = 100;
    public const int MaxPages = 10;

    public async Task<ClaudeApiModelsDto> ListModelsAsync(CancellationToken ct = default)
    {
        var apiKey = await keys.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ClaudeApiModelsDto.NoKey;
        }

        var timeout = options.Value.CatalogTimeout;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);
        try
        {
            var (models, complete) = await ListAllAsync(apiKey, timeout, budget.Token);
            return ClaudeApiModelsDto.Listed(models, complete ? null : $"Showing the first {models.Count} models; the Claude API has more.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && Describe(ex, timeout) is { } error)
        {
            logger.LogInformation("Claude API model list failed ({Error})", ex.GetType().Name);
            return ClaudeApiModelsDto.Unreachable(error);
        }
    }

    /// <summary>Stops when there are no more pages, the cursor does not move, or after <see cref="MaxPages"/> (incomplete).</summary>
    private async Task<(List<ClaudeApiModelDto> Models, bool Complete)> ListAllAsync(string apiKey, TimeSpan timeout, CancellationToken ct)
    {
        using var anthropic = ClaudeApiChat.CreateAnthropic(httpClients, ClaudeApiHttp.CatalogClientName, options.Value, apiKey, timeout);
        var models = new List<ClaudeApiModelDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? afterId = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var list = await anthropic.Models.List(new ModelListParams { Limit = PageSize, AfterID = afterId }, ct);
            foreach (var model in list.Items.Where(m => !string.IsNullOrWhiteSpace(m.ID) && seen.Add(m.ID)))
            {
                models.Add(new ClaudeApiModelDto(model.ID, string.IsNullOrWhiteSpace(model.DisplayName) ? model.ID : model.DisplayName, model.CreatedAt));
            }

            var lastId = list.Items.Count > 0 ? list.Items[^1].ID : null;
            if (!list.HasNext() || string.IsNullOrEmpty(lastId) || lastId == afterId)
            {
                return (models, true);
            }

            afterId = lastId;
        }

        return (models, false);
    }

    /// <summary>The readable error for a failed list; <c>null</c> for anything that is not an API or network failure.</summary>
    private static string? Describe(Exception ex, TimeSpan timeout) =>
        ClaudeApiErrorClient.Map(ex, timeout, CancellationToken.None)?.Message
        ?? (ex is OperationCanceledException ? $"No answer from the Claude API within {timeout.TotalSeconds:0} s." : null);
}
