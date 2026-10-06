using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Llm;

/// <summary>Runs one tiny request against a model to prove it loads and answers.</summary>
public sealed class LlmModelTester(
    ILlmClientFactory factory,
    IOptions<LlmOptions> options,
    TimeProvider time,
    ILogger<LlmModelTester> logger)
{
    public const string ChatPrompt = """Reply with exactly this JSON and nothing else: {"ok":true}""";
    public const string EmbeddingInput = "hello";

    /// <param name="kind">A validated <see cref="ModelKinds"/> value.</param>
    public async Task<TestModelResultDto> TestAsync(string kind, string model, Uri baseUrl, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        string? error;
        try
        {
            error = kind == ModelKinds.Chat
                ? await TestChatAsync(baseUrl, model, ct)
                : await TestEmbeddingAsync(baseUrl, model, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation("Model test failed ({Kind}, {Error})", kind, ex.GetType().Name);
            error = Describe(ex, baseUrl);
        }

        var elapsed = (long)time.GetElapsedTime(started).TotalMilliseconds;
        return new TestModelResultDto(error is null, elapsed, error);
    }

    private async Task<string?> TestChatAsync(Uri baseUrl, string model, CancellationToken ct)
    {
        using var client = factory.CreateChatClient(baseUrl, model);
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, ChatPrompt)],
            new ChatOptions { ResponseFormat = ChatResponseFormat.Json, Temperature = 0 },
            ct);
        return IsJsonObject(response.Text) ? null : "The model answered, but not with a JSON object.";
    }

    private async Task<string?> TestEmbeddingAsync(Uri baseUrl, string model, CancellationToken ct)
    {
        using var generator = factory.CreateEmbeddingGenerator(baseUrl, model);
        var embeddings = await generator.GenerateAsync([EmbeddingInput], cancellationToken: ct);
        return embeddings.Count == 1 && embeddings[0].Vector.Length > 0 ? null : "The model returned no embedding.";
    }

    private static bool IsJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Ollama reports a missing or unsuitable model as an HTTP error with a message; keep that message.
    private string Describe(Exception ex, Uri baseUrl) => ex switch
    {
        HttpRequestException { StatusCode: not null } or OllamaSharp.Models.Exceptions.OllamaException
            => $"The model call failed: {Truncate(ex.Message)}",
        _ when OllamaErrors.IsConnectionFailure(ex) => OllamaErrors.Describe(ex, baseUrl, options.Value.ModelTimeout),
        _ => $"The model call failed: {Truncate(ex.Message)}",
    };

    private static string Truncate(string message) => message.Length <= 300 ? message : message[..300] + "…";
}
