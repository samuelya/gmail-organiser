using GmailOrganiser.Llm;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>
/// Hands out the given fakes (never disposes them for real) and records the requested server and model (or Claude API model).
/// <see cref="EnsureChatConfiguredAsync"/> runs the real check of <paramref name="checks"/> (see <see cref="Checks(ISettingsStore)"/>).
/// </summary>
public sealed class FakeLlmClientFactory(
    FakeChatClient? chat = null, FakeEmbeddingGenerator? embed = null, ILlmClientFactory? checks = null) : ILlmClientFactory
{
    public FakeChatClient Chat { get; } = chat ?? new FakeChatClient();
    public FakeEmbeddingGenerator Embeddings { get; } = embed ?? new FakeEmbeddingGenerator();
    public List<(Uri BaseUrl, string Model)> Targets { get; } = [];
    public List<string> ClaudeApiTestModels { get; } = [];

    /// <summary>A real factory (without <c>LLM_FAKE</c>) whose start check reads <paramref name="settings"/>.</summary>
    public static LlmClientFactory Checks(ISettingsStore settings) => new(
        new StubHttpClientFactory(new StubOllamaHandler()), settings, TestClaudeApiKeys.For(settings), Options.Create(new LlmOptions()));

    /// <summary>A real factory from a test host's services, so its start check reads the host's settings and key ring.</summary>
    public static LlmClientFactory Checks(IServiceProvider services) => ActivatorUtilities.CreateInstance<LlmClientFactory>(services);

    public Task<IChatClient> CreateChatClientAsync(CancellationToken ct = default) => Task.FromResult<IChatClient>(Chat);

    public Task<ChatConfiguration> EnsureChatConfiguredAsync(CancellationToken ct = default) =>
        (checks ?? throw new NotSupportedException("Pass the checks factory to run the start check.")).EnsureChatConfiguredAsync(ct);

    public IChatClient CreateChatClient(ChatConfiguration chat) => Chat;

    public Task<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(CancellationToken ct = default) =>
        Task.FromResult<IEmbeddingGenerator<string, Embedding<float>>>(Embeddings);

    public IChatClient CreateChatClient(Uri baseUrl, string model)
    {
        Targets.Add((baseUrl, model));
        return Chat;
    }

    public IChatClient CreateClaudeApiTestChatClient(string apiKey, string model)
    {
        ClaudeApiTestModels.Add(model);
        return Chat;
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(Uri baseUrl, string model)
    {
        Targets.Add((baseUrl, model));
        return Embeddings;
    }
}
