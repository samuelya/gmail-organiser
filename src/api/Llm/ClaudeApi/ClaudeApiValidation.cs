using System.Text.Json;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Llm.ClaudeApi;

/// <summary>Checks for the Claude API provider settings and key. Messages never include the submitted value.</summary>
public static class ClaudeApiValidation
{
    public const int MinKeyLength = 20;
    public const int MaxKeyLength = 300;

    /// <summary>Adds field errors for <c>llmProvider</c> and <c>claudeApiModel</c>.</summary>
    public static void Check(Dictionary<string, string[]> errors, UpdateSettingsRequest request)
    {
        if (request.LlmProvider is { } raw && ParseProvider(raw) is null)
        {
            errors["llmProvider"] = ["Must be ollama or claude_api."];
        }

        ModelNameValidation.Check(errors, "claudeApiModel", request.ClaudeApiModel);
    }

    /// <summary>
    /// The provider a raw <c>llmProvider</c> value names (<c>ollama | claude_api</c>), or <c>null</c> for anything else.
    /// The request carries the raw JSON so a wrong value or type is a field error, not a deserialisation failure.
    /// </summary>
    public static LlmProvider? ParseProvider(JsonElement raw) =>
        raw.ValueKind != JsonValueKind.String ? null : raw.GetString() switch
        {
            "ollama" => LlmProvider.Ollama,
            "claude_api" => LlmProvider.ClaudeApi,
            _ => null,
        };

    /// <summary>The trimmed key, or field errors; exactly one of the two is non-null.</summary>
    public static (string? Key, Dictionary<string, string[]>? Errors) Validate(SetClaudeApiKeyRequest request) =>
        request.ApiKey?.Trim() is { Length: >= MinKeyLength and <= MaxKeyLength } key && key.All(c => c is > ' ' and < '\x7f')
            ? (key, null)
            : (null, new() { ["apiKey"] = [$"Must be {MinKeyLength}–{MaxKeyLength} printable characters without spaces."] });
}
