using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Policies.Prompts;

/// <summary>
/// Reading a rule's match: the fields, the profile template the model copied, and the default rule name; and the same
/// rule checks for a rule learned from an approved suggestion.
/// </summary>
public sealed partial class SenderPolicyOutputParser
{
    /// <summary>
    /// The checks a model's rule passes, for a rule built from an approved suggestion instead (#361). Null when refused:
    /// an empty or unusable match (a value over <see cref="MaxMatchValueLength"/> or with control characters), an
    /// unusable or reserved topic label, or delete or unsubscribe on a subject naming a transactional document. An
    /// allowlisted sender's rule keeps; a document type that no longer fits the parent in <paramref name="settings"/> is
    /// dropped. Sets <see cref="SenderPolicyRuleRow.Name"/> when empty.
    /// </summary>
    public SenderPolicyRuleRow? CheckLearned(SenderPolicyRuleRow rule, bool allowlisted, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(settings);
        var match = rule.Match;
        if (match.IsEmpty || !UsableValue(match.FromAddress) || !UsableValue(match.FromSubdomain)
            || !UsableValue(match.SubjectTemplate) || !UsableValue(match.SubjectContains))
        {
            return null;
        }

        rule.TopicLabel = rule.TopicLabel.Trim();
        if (!IsUsableLabel(rule.TopicLabel, settings) || IsTransactionalDelete(match, rule.Action))
        {
            return null;
        }

        if (allowlisted)
        {
            rule.Action = PolicyAction.Keep;
        }

        var parent = string.IsNullOrWhiteSpace(settings.DocumentTypeParent) ? null : settings.DocumentTypeParent.Trim();
        rule.DocumentTypeLabel = rule.DocumentTypeLabel is { } documentType && parent is not null && !IsConfiguredLabel(documentType, settings)
            ? DocumentTypePath.Normalise(documentType.Trim(), parent, rule.TopicLabel, out _)
            : null;
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            rule.Name = Describe(match);
        }

        // Backstop: the rules every stored policy keeps, on a mixed shell holding only this rule.
        return PolicyValidation.Validate(new SenderPolicyRow { IsMixed = true, Action = PolicyAction.Archive, Rules = [rule] }).Count == 0
            ? rule
            : null;

        static bool UsableValue(string? value) =>
            value is null || (value.Length <= MaxMatchValueLength && !value.Any(char.IsControl));
    }

    /// <summary>Transactional mail is never deleted and never unsubscribed (DESIGN §6.2); checked on the full subject criteria.</summary>
    private bool IsTransactionalDelete(RuleMatch match, PolicyAction action) =>
        action is PolicyAction.Delete or PolicyAction.Unsubscribe
        && (guard.HasKeyword(match.SubjectTemplate) || guard.HasKeyword(match.SubjectContains));

    /// <summary>A valid label path that is neither a system label nor the configured delete or action label.</summary>
    private static bool IsUsableLabel(string label, AppSettings settings) =>
        LabelPath.IsValid(label) && !LabelPath.IsReserved(label) && !IsConfiguredLabel(label, settings);

    private static bool IsConfiguredLabel(string label, AppSettings settings) =>
        string.Equals(label.Trim(), settings.DeleteLabelName.Trim(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(label.Trim(), settings.ActionLabelName.Trim(), StringComparison.OrdinalIgnoreCase);

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
