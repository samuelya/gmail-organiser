namespace GmailOrganiser.Llm;

/// <summary>
/// HTTP timeouts for Ollama, bound from the <c>Llm</c> configuration section; <c>LLM_FAKE</c> (from <c>.env</c>)
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

    /// <summary>For chat and embedding calls, including model load time.</summary>
    public int ModelTimeoutSeconds { get; set; } = 180;

    public TimeSpan CatalogTimeout => TimeSpan.FromSeconds(Math.Max(1, CatalogTimeoutSeconds));
    public TimeSpan ModelTimeout => TimeSpan.FromSeconds(Math.Max(1, ModelTimeoutSeconds));
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
