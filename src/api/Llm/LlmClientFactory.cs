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
    /// <exception cref="LlmNotConfiguredException">No chat model is chosen.</exception>
    Task<IChatClient> CreateChatClientAsync(CancellationToken ct = default);

    /// <exception cref="LlmNotConfiguredException">No embedding model is chosen.</exception>
    Task<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(CancellationToken ct = default);

    /// <summary>A client for an explicit server and model, e.g. to test them before saving.</summary>
    IChatClient CreateChatClient(Uri baseUrl, string model);

    /// <summary>A generator for an explicit server and model, e.g. to test them before saving.</summary>
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(Uri baseUrl, string model);
}

public sealed class LlmClientFactory(
    IHttpClientFactory httpClients,
    ISettingsStore settings,
    IOptions<LlmOptions> options) : ILlmClientFactory
{
    /// <summary>Vector size of the <c>LLM_FAKE=true</c> embeddings.</summary>
    public const int FakeEmbeddingDimension = 768;

    public async Task<IChatClient> CreateChatClientAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        var model = s.ChatModel ?? throw new LlmNotConfiguredException(ModelKinds.Chat);
        return UseFake ? CreateFakeChatClient() : CreateChatClient(OllamaHttp.Parse(s.OllamaBaseUrl), model);
    }

    public async Task<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        var model = s.EmbeddingModel ?? throw new LlmNotConfiguredException(ModelKinds.Embedding);
        return UseFake ? CreateFakeEmbeddingGenerator() : CreateEmbeddingGenerator(OllamaHttp.Parse(s.OllamaBaseUrl), model);
    }

    public IChatClient CreateChatClient(Uri baseUrl, string model) => UseFake ? CreateFakeChatClient() : Create(baseUrl, model);

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(Uri baseUrl, string model) =>
        UseFake ? CreateFakeEmbeddingGenerator() : Create(baseUrl, model);

    private bool UseFake => options.Value.UseFake;

    // A new fake per call: callers dispose it, and its request log lives no longer than one run.
    private static FakeChatClient CreateFakeChatClient() => new() { Responder = FakeAnalysisResponder.Answer };

    private static FakeEmbeddingGenerator CreateFakeEmbeddingGenerator() => new(FakeEmbeddingDimension);

    // OllamaApiClient implements both IChatClient and IEmbeddingGenerator; its HttpClient comes from IHttpClientFactory,
    // so disposing the client never disposes a pooled handler.
    private OllamaApiClient Create(Uri baseUrl, string model) =>
        new(OllamaHttp.Create(httpClients, baseUrl, options.Value.ModelTimeout), model);
}

/// <summary>No model of <see cref="Kind"/> is chosen in Settings; endpoints map it to 409 ProblemDetails.</summary>
public sealed class LlmNotConfiguredException(string kind)
    : InvalidOperationException($"No {kind} model is selected. Choose one in Settings.")
{
    public string Kind { get; } = kind;
}
