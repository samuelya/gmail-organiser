using System.Text.Json;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;

namespace GmailOrganiser.Policies.Prompts;

/// <summary>Reading a rule's match: the fields, the profile template the model copied, and the default rule name.</summary>
public sealed partial class SenderPolicyOutputParser
{
    /// <summary>
    /// The match fields; null or an empty string leaves a field unset. Any other value that is of the wrong type,
    /// unknown or over-long makes the match unusable: null, with <paramref name="invalid"/> naming the field.
    /// </summary>
    private static RuleMatch? ReadMatch(JsonElement m, out string? invalid)
    {
        string? bad = null;
        T? Check<T>(string name, T? value)
        {
            if (value is null && m.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
                && !(v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())))
            {
                bad ??= name;
            }

            return value;
        }

        bool? Flag(string name) => Check(name,
            m.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : (bool?)null);
        string? Text(string name) => Check(name, ReadText(m, name, MaxMatchValueLength, clip: false));

        var match = new RuleMatch
        {
            ListIdPresent = Flag("listIdPresent"),
            ListUnsubscribePresent = Flag("listUnsubscribePresent"),
            FromAddress = Text("fromAddress"),
            FromSubdomain = Text("fromSubdomain"),
            Category = Check("category", ReadEnum<MessageCategory>(m, "category", out _)),
            SubjectTemplate = Text("subjectTemplate"),
            SubjectContains = Text("subjectContains"),
        };
        invalid = bad;
        return bad is null ? match : null;
    }

    private static string Describe(RuleMatch m)
    {
        var parts = new[]
        {
            m.ListIdPresent is { } l ? (l ? "list" : "not a list") : null,
            m.ListUnsubscribePresent is { } u ? (u ? "unsubscribe header" : "no unsubscribe header") : null,
            m.FromAddress is { } a ? "from " + a : null,
            m.FromSubdomain is { } d ? "from *." + d : null,
            m.Category is { } c ? "category " + SnakeCaseEnumConverter<MessageCategory>.ToDb(c) : null,
            m.SubjectTemplate is { } t ? $"subject \"{t}\"" : null,
            m.SubjectContains is { } s ? $"subject contains \"{s}\"" : null,
        };
        return SuggestionOutputParser.Cut(string.Join(", ", parts.OfType<string>()), MaxNameLength);
    }

    /// <summary>
    /// The profile template the model copied, as stored: the profile quotes templates and clips them with an ellipsis,
    /// while <see cref="PolicyMatcher"/> compares the full template. A clipped value resolves when exactly one profile
    /// template starts with it. Null when no profile template matches.
    /// </summary>
    private static string? ResolveTemplate(string value, SenderProfile profile)
    {
        var text = value.Trim().Trim('"', '“', '”').Trim();
        var clipped = text.EndsWith(Ellipsis, StringComparison.Ordinal);
        if (clipped)
        {
            text = text[..^Ellipsis.Length].TrimEnd();
        }

        var candidates = profile.Templates.Select(t => t.Template)
            .Where(t => clipped
                ? text.Length > 0 && t.StartsWith(text, StringComparison.OrdinalIgnoreCase)
                : string.Equals(t, text, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }
}
