namespace GmailOrganiser.Llm;

/// <summary>One locally installed Ollama model. <see cref="Capabilities"/> is empty when <c>/api/show</c> gave none.</summary>
public sealed record OllamaModelDto(
    string Name,
    long SizeBytes,
    string? Family,
    string? ParameterSize,
    IReadOnlyList<string> Capabilities);

/// <summary>
/// Models split by capability. A model without known capabilities is in both lists.
/// An unreachable server is <c>Reachable = false</c> with a readable <see cref="Error"/>, never a 500.
/// </summary>
public sealed record LlmModelsDto(
    bool Reachable,
    string? Version,
    IReadOnlyList<OllamaModelDto> ChatModels,
    IReadOnlyList<OllamaModelDto> EmbeddingModels,
    string? Error);

/// <summary><see cref="Kind"/> is <c>chat</c> or <c>embedding</c>; <see cref="BaseUrl"/> overrides the saved URL.</summary>
public sealed record TestModelRequest(string? Kind, string? Model, string? BaseUrl);

/// <summary><see cref="ElapsedMs"/> includes the time Ollama needs to load the model.</summary>
public sealed record TestModelResultDto(bool Ok, long ElapsedMs, string? Error);

public static class ModelKinds
{
    public const string Chat = "chat";
    public const string Embedding = "embedding";
}

public static class OllamaCapabilities
{
    public const string Completion = "completion";
    public const string Embedding = "embedding";
}
