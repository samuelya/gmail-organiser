using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmailOrganiser.Llm;

/// <summary>Who answers the analysis prompts (#484): the local Ollama models or the Claude API.</summary>
[JsonConverter(typeof(LlmProviderJsonConverter))]
public enum LlmProvider
{
    Ollama,
    ClaudeApi,
}

/// <summary>Serialises <see cref="LlmProvider"/> as <c>ollama | claude_api</c> (API and settings document).</summary>
public sealed class LlmProviderJsonConverter()
    : JsonStringEnumConverter<LlmProvider>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false);
