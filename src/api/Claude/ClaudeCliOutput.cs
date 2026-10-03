using System.Text.Json;
using System.Text.RegularExpressions;

namespace GmailOrganiser.Claude;

/// <summary>
/// The <c>--output-format json</c> result of <c>claude -p</c>: <c>{"type":"result","subtype","is_error","result",
/// "num_turns","modelUsage":{"&lt;model&gt;":{…}}, …}</c>. Parsed tolerantly: unknown fields are ignored, a missing
/// field is null, log lines before the JSON are skipped and an array of messages (<c>--verbose</c>) yields its last
/// <c>result</c> message. The owner check on #166 confirms the fields against a real run.
/// </summary>
public sealed partial record ClaudeCliOutput(bool IsError, string? Subtype, string? Result, int? NumTurns, string? Model)
{
    public const int MaxErrorLength = 300;

    /// <summary>The result message in <paramref name="stdout"/>; null when there is none.</summary>
    public static ClaudeCliOutput? Parse(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        var trimmed = stdout.Trim();
        var candidates = new[] { trimmed }.Concat(trimmed.Split('\n').Reverse().Select(l => l.Trim()))
            .Where(c => c.StartsWith('{') || c.StartsWith('['));
        foreach (var candidate in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (FromElement(doc.RootElement) is { } output)
                {
                    return output;
                }
            }
            catch (JsonException)
            {
                // Not JSON: try the next line.
            }
        }

        return null;
    }

    /// <summary>The error kind for a failed run's text: <see cref="ClaudeErrorKinds.Auth"/>, <see cref="ClaudeErrorKinds.RateLimit"/> or <see cref="ClaudeErrorKinds.Failed"/>.</summary>
    public static string Classify(string? text) => text switch
    {
        null => ClaudeErrorKinds.Failed,
        _ when AuthPattern().IsMatch(text) => ClaudeErrorKinds.Auth,
        _ when RateLimitPattern().IsMatch(text) => ClaudeErrorKinds.RateLimit,
        _ => ClaudeErrorKinds.Failed,
    };

    /// <summary>
    /// One line of at most <see cref="MaxErrorLength"/> characters with bearer values, token-like strings, URL
    /// query strings and every <paramref name="secrets"/> value masked; null when nothing is left.
    /// </summary>
    public static string? Sanitise(string? text, params string?[] secrets)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        text = BearerPattern().Replace(text, "Bearer ***");
        text = QueryPattern().Replace(text, "?***");
        text = TokenLikePattern().Replace(text, "***");
        text = WhitespacePattern().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text.Length <= MaxErrorLength ? text : text[..(MaxErrorLength - 1)] + "…";
    }

    private static ClaudeCliOutput? FromElement(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().Reverse().Select(FromElement).FirstOrDefault(o => o is not null);
        }

        if (root.ValueKind != JsonValueKind.Object
            || (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() != "result"))
        {
            return null;
        }

        var subtype = String(root, "subtype");
        var isError = (root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
            || (subtype is not null && subtype.StartsWith("error", StringComparison.Ordinal));
        var turns = root.TryGetProperty("num_turns", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var t) ? t : (int?)null;
        return new ClaudeCliOutput(isError, subtype, String(root, "result"), turns, String(root, "model") ?? MainModel(root));
    }

    /// <summary>The <c>modelUsage</c> key with the most output tokens (small helper models also appear there).</summary>
    private static string? MainModel(JsonElement root) =>
        root.TryGetProperty("modelUsage", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? usage.EnumerateObject()
                .OrderByDescending(p => p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("outputTokens", out var o)
                    && o.TryGetDouble(out var d) ? d : 0)
                .Select(p => p.Name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            : null;

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"\b(401|403|unauthori[sz]ed|authenticat\w*|invalid (api|x-api|oauth)|oauth token|token (has )?expired|expired token|/login|log ?in again|setup-token)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AuthPattern();

    [GeneratedRegex(@"\b(429|rate.?limit\w*|usage limit|limit reached|too many requests|quota)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimitPattern();

    [GeneratedRegex(@"Bearer\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\?[^\s""']+")]
    private static partial Regex QueryPattern();

    [GeneratedRegex(@"\b(sk-ant-[\w-]+|[A-Za-z0-9_\-]{32,})")]
    private static partial Regex TokenLikePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
