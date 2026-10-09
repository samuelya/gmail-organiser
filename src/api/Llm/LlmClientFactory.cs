using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace GmailOrganiser.Llm;

/// <summary>
/// The only way to get an LLM client. The settings are read on every call, so a model or URL change applies
/// without a restart. Callers dispose the returned client.
/// </summary>
public interface ILlmClientFactory
{
    /// <summary>A chat client for the provider chosen in Settings (Ollama or the Claude API).</summary>
    /// <exception cref="LlmNotConfiguredException">No chat model is chosen, or no Claude API key is set.</exception>
    Task<IChatClient> CreateChatClientAsync(CancellationToken ct = default);

    /// <exception cref="LlmNotConfiguredException">No embedding model is chosen.</exception>
    Task<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(CancellationToken ct = default);

    /// <summary>A client for an explicit server and model, e.g. to test them before saving.</summary>
    IChatClient CreateChatClient(Uri baseUrl, string model);

    /// <summary>A Claude API client for an explicit key and model, e.g. to test them before saving.</summary>
    IChatClient CreateClaudeApiChatClient(string apiKey, string model);

    /// <summary>A generator for an explicit server and model, e.g. to test them before saving.</summary>
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(Uri baseUrl, string model);
}

public sealed class LlmClientFactory(
    IHttpClientFactory httpClients,
    ISettingsStore settings,
    ClaudeApiKeyService claudeApiKey,
    IOptions<LlmOptions> options) : ILlmClientFactory
{
    public const string NoClaudeApiKeyMessage = "No Claude API key is set. Add one in Settings.";

    /// <summary>Vector size of the <c>LLM_FAKE=true</c> embeddings.</summary>
    public const int FakeEmbeddingDimension = 768;

    public async Task<IChatClient> CreateChatClientAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        var model = string.IsNullOrWhiteSpace(s.ActiveChatModel) ? throw new LlmNotConfiguredException(ModelKinds.Chat) : s.ActiveChatModel;
        if (UseFake)
        {
            return CreateFakeChatClient();
        }

        if (s.LlmProvider == LlmProvider.ClaudeApi)
        {
            var apiKey = await claudeApiKey.GetAsync(s, ct);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new LlmNotConfiguredException(ModelKinds.Chat, NoClaudeApiKeyMessage);
            }

            return CreateClaudeApiChatClient(apiKey, model);
        }

        return CreateChatClient(OllamaHttp.Parse(s.OllamaBaseUrl), model);
    }

    public async Task<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        var model = s.EmbeddingModel ?? throw new LlmNotConfiguredException(ModelKinds.Embedding);
        return UseFake ? CreateFakeEmbeddingGenerator() : CreateEmbeddingGenerator(OllamaHttp.Parse(s.OllamaBaseUrl), model);
    }

    public IChatClient CreateChatClient(Uri baseUrl, string model) =>
        UseFake ? CreateFakeChatClient() : CreateOllamaChatClient(baseUrl, model);

    public IChatClient CreateClaudeApiChatClient(string apiKey, string model) =>
        UseFake ? CreateFakeChatClient() : ClaudeApiChat.Create(httpClients, options.Value, apiKey, model);

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(Uri baseUrl, string model) =>
        UseFake ? CreateFakeEmbeddingGenerator() : Create(baseUrl, model);

    private bool UseFake => options.Value.UseFake;

    // A new fake per call: callers dispose it, and its request log lives no longer than one run.
    private static FakeChatClient CreateFakeChatClient() =>
        new() { Responder = m => FakeTaxonomyResponder.Answer(m) ?? FakeAnalysisResponder.Answer(m) };

    // Reports the fake model name, so memory never records fake vectors under the real model chosen in Settings.
    private static FakeEmbeddingGenerator CreateFakeEmbeddingGenerator() =>
        new(FakeEmbeddingDimension) { ModelId = FakeOllamaCatalog.EmbeddingModel };

    // Every chat call (analysis, policies, taxonomy, rules summary, vision, model test) sends think:false. The
    // pipeline disposes the inner client.
    private IChatClient CreateOllamaChatClient(Uri baseUrl, string model) =>
        new ChatClientBuilder(Create(baseUrl, model)).ConfigureOptions(OllamaRequestOptions.NoThink).Build();

    // OllamaApiClient implements both IChatClient and IEmbeddingGenerator; its HttpClient comes from IHttpClientFactory,
    // so disposing the client never disposes a pooled handler.
    private OllamaApiClient Create(Uri baseUrl, string model) =>
        new(OllamaHttp.Create(httpClients, baseUrl, options.Value.ModelTimeout), model);
}

/// <summary>
/// No model of <see cref="Kind"/> is chosen in Settings, or another setting it needs is missing (<paramref name="message"/>);
/// endpoints map it to 409 ProblemDetails.
/// </summary>
public sealed class LlmNotConfiguredException(string kind, string? message = null)
    : InvalidOperationException(message ?? $"No {kind} model is selected. Choose one in Settings.")
{
    public string Kind { get; } = kind;
}
