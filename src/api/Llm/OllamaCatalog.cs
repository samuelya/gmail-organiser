using GmailOrganiser.Settings;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace GmailOrganiser.Llm;

/// <summary>Reads what an Ollama server offers. <c>baseUrl</c> overrides the saved URL so an unsaved URL can be tested.</summary>
public interface IOllamaCatalog
{
    /// <summary><c>/api/tags</c>, then <c>/api/show</c> per model (at most four at a time).</summary>
    /// <exception cref="OllamaUnreachableException">The server could not be reached or is not Ollama.</exception>
    Task<IReadOnlyList<OllamaModelDto>> ListModelsAsync(string? baseUrl = null, CancellationToken ct = default);

    /// <summary><c>/api/version</c>; returns the server version.</summary>
    /// <exception cref="OllamaUnreachableException">The server could not be reached or is not Ollama.</exception>
    Task<string> PingAsync(string? baseUrl = null, CancellationToken ct = default);
}

public sealed class OllamaCatalog(
    IHttpClientFactory httpClients,
    ISettingsStore settings,
    IOptions<LlmOptions> options,
    ILogger<OllamaCatalog> logger) : IOllamaCatalog
{
    public const int MaxParallelShows = 4;

    public async Task<IReadOnlyList<OllamaModelDto>> ListModelsAsync(string? baseUrl = null, CancellationToken ct = default)
    {
        var url = await ResolveAsync(baseUrl, ct);
        using var client = CreateClient(url);
        var models = (await Call(url, () => client.ListLocalModelsAsync(ct), ct)).ToList();

        var result = new OllamaModelDto[models.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, models.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelShows, CancellationToken = ct },
            async (i, token) =>
            {
                var model = models[i];
                var capabilities = await ShowCapabilitiesAsync(client, model.Name, token);
                result[i] = new OllamaModelDto(model.Name, model.Size, model.Details?.Family, model.Details?.ParameterSize, capabilities);
            });
        return result;
    }

    public async Task<string> PingAsync(string? baseUrl = null, CancellationToken ct = default)
    {
        var url = await ResolveAsync(baseUrl, ct);
        using var client = CreateClient(url);
        var version = await Call(url, () => client.GetVersionAsync(ct), ct);
        return version?.ToString() ?? "";
    }

    /// <summary>The explicit URL (already validated) or the saved one.</summary>
    public static async Task<Uri> ResolveBaseUrlAsync(string? baseUrl, ISettingsStore settings, CancellationToken ct) =>
        OllamaHttp.Parse(string.IsNullOrWhiteSpace(baseUrl) ? (await settings.GetAsync(ct)).OllamaBaseUrl : baseUrl);

    private Task<Uri> ResolveAsync(string? baseUrl, CancellationToken ct) => ResolveBaseUrlAsync(baseUrl, settings, ct);

    private OllamaApiClient CreateClient(Uri url) =>
        new(OllamaHttp.Create(httpClients, url, options.Value.CatalogTimeout));

    // A failing /api/show only loses the capabilities: the model then counts as both chat and embedding.
    private async Task<IReadOnlyList<string>> ShowCapabilitiesAsync(OllamaApiClient client, string model, CancellationToken ct)
    {
        try
        {
            var show = await client.ShowModelAsync(model, ct);
            return show.Capabilities?.Where(c => !string.IsNullOrWhiteSpace(c)).ToArray() ?? [];
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Ollama /api/show failed for a model ({Error}); listing it without capabilities", ex.GetType().Name);
            return [];
        }
    }

    private async Task<T> Call<T>(Uri url, Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && OllamaErrors.IsConnectionFailure(ex))
        {
            throw new OllamaUnreachableException(OllamaErrors.Describe(ex, url, options.Value.CatalogTimeout), ex);
        }
    }
}

public sealed class OllamaUnreachableException(string message, Exception inner) : Exception(message, inner);
