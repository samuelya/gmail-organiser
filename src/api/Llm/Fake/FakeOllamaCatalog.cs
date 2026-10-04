namespace GmailOrganiser.Llm.Fake;

/// <summary>The catalog of <c>LLM_FAKE=true</c>: one chat and one embedding model, no vision model, no HTTP.</summary>
public sealed class FakeOllamaCatalog : IOllamaCatalog
{
    public const string Version = "fake";
    public const string ChatModel = "fake-chat";
    public const string EmbeddingModel = "fake-embed";

    private static readonly IReadOnlyList<OllamaModelDto> Models =
    [
        new(ChatModel, 0, "fake", null, [OllamaCapabilities.Completion]),
        new(EmbeddingModel, 0, "fake", null, [OllamaCapabilities.Embedding]),
    ];

    public Task<IReadOnlyList<OllamaModelDto>> ListModelsAsync(string? baseUrl = null, CancellationToken ct = default) =>
        Task.FromResult(Models);

    public Task<string> PingAsync(string? baseUrl = null, CancellationToken ct = default) => Task.FromResult(Version);
}
