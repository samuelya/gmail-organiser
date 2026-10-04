using GmailOrganiser.Llm;
using GmailOrganiser.Llm.Fake;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>Hands out the given fakes (never disposes them for real) and records the requested server and model.</summary>
public sealed class FakeLlmClientFactory(FakeChatClient? chat = null, FakeEmbeddingGenerator? embed = null) : ILlmClientFactory
{
    public FakeChatClient Chat { get; } = chat ?? new FakeChatClient();
    public FakeEmbeddingGenerator Embeddings { get; } = embed ?? new FakeEmbeddingGenerator();
    public List<(Uri BaseUrl, string Model)> Targets { get; } = [];

    public Task<IChatClient> CreateChatClientAsync(CancellationToken ct = default) => Task.FromResult<IChatClient>(Chat);

    public Task<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(CancellationToken ct = default) =>
        Task.FromResult<IEmbeddingGenerator<string, Embedding<float>>>(Embeddings);

    public IChatClient CreateChatClient(Uri baseUrl, string model)
    {
        Targets.Add((baseUrl, model));
        return Chat;
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(Uri baseUrl, string model)
    {
        Targets.Add((baseUrl, model));
        return Embeddings;
    }
}
