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
    string? Error)
{
    /// <summary>Chat = <c>completion</c> capability, embedding = <c>embedding</c>; no known capabilities = both.</summary>
    public static LlmModelsDto Split(string? version, IReadOnlyList<OllamaModelDto> models) => new(
        true,
        version,
        [.. models.Where(m => Has(m, OllamaCapabilities.Completion))],
        [.. models.Where(m => Has(m, OllamaCapabilities.Embedding))],
        null);

    public static LlmModelsDto Unreachable(string error) => new(false, null, [], [], error);

    private static bool Has(OllamaModelDto model, string capability) =>
        model.Capabilities.Count == 0 || model.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);
}

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

/// <summary>One model the saved Claude API key can use; <see cref="CreatedAt"/> is the release date Anthropic reports.</summary>
public sealed record ClaudeApiModelDto(string Id, string DisplayName, DateTimeOffset? CreatedAt);

/// <summary>
/// The Claude API models, newest first. No key is <c>KeySet = false</c> with no models; a failed call is
/// <c>Reachable = false</c> with a readable <see cref="Error"/>, never a 500. A list cut short at the page cap is
/// <c>Reachable = true</c> with a note in <see cref="Error"/>.
/// </summary>
public sealed record ClaudeApiModelsDto(bool KeySet, bool Reachable, string? Error, IReadOnlyList<ClaudeApiModelDto> Models)
{
    public static ClaudeApiModelsDto NoKey { get; } = new(false, false, null, []);

    public static ClaudeApiModelsDto Unreachable(string error) => new(true, false, error, []);

    /// <summary>Newest first; ties (and models without a date, last) by id.</summary>
    public static ClaudeApiModelsDto Listed(IEnumerable<ClaudeApiModelDto> models, string? note = null) => new(
        true,
        true,
        note,
        [.. models.OrderByDescending(m => m.CreatedAt.HasValue).ThenByDescending(m => m.CreatedAt).ThenBy(m => m.Id, StringComparer.Ordinal)]);
}

public sealed record TestClaudeApiModelRequest(string? Model);
