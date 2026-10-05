using System.Text;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Policies.Prompts;

/// <summary>
/// The parsed answer of one policy prompt. <see cref="Policy"/> is null when <see cref="Errors"/> is not empty: the caller
/// records a failure. <see cref="Dropped"/> lists what the parser removed or changed. The caller sets the ids,
/// <c>RunId</c>, <c>Model</c> and <c>CreatedAt</c> of the policy and its rules.
/// </summary>
public sealed record ParsedPolicy(
    SenderPolicyRow? Policy, IReadOnlyList<SenderPolicyRuleRow> Rules, IReadOnlyList<string> Errors, IReadOnlyList<string> Dropped)
{
    /// <summary>The topic and document-type labels of the policy and its rules that are not in the label tree.</summary>
    public IReadOnlyList<string> NewLabels { get; init; } = [];

    /// <summary>The policy's topic label is new, recomputed from the label tree; the model's <c>isNewLabel</c> is ignored.</summary>
    public bool IsNewLabel => Policy?.TopicLabel is { } label && NewLabels.Contains(label, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Validates the model's answer to the sender-policy prompt and enforces the safety rules itself instead of trusting the
/// model (DESIGN §6.2). Never throws on model output; errors and notes name fields and rule numbers, never email content.
/// </summary>
public sealed class SenderPolicyOutputParser(TransactionalGuard guard)
{
    public const int MaxRules = 8;
    public const int MaxNameLength = 100;
    public const int MaxMatchValueLength = 200;

    private const int MaxCandidates = 32;
    private const string InvalidText = "Output contains a string that is not valid UTF-16 text.";

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public ParsedPolicy Parse(string? json, SenderProfile profile, LabelTreeIndex labelTree)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(labelTree);
        var errors = new List<string>();
        var dropped = new List<string>();
        using var document = ReadFirstObject(json ?? "");
        if (document is null)
        {
            errors.Add("Output is not a JSON object.");
            return new ParsedPolicy(null, [], errors, dropped);
        }

        try
        {
            return Read(document.RootElement, profile, labelTree, errors, dropped);
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate escape (\ud800) in a name or string: System.Text.Json throws on reading it.
            errors.Add(InvalidText);
            return new ParsedPolicy(null, [], errors, dropped);
        }
    }

    private ParsedPolicy Read(JsonElement root, SenderProfile profile, LabelTreeIndex tree, List<string> errors, List<string> dropped)
    {
        var isMixed = ReadBool(root, "isMixed", "", errors) ?? false;
        var action = ReadEnum<PolicyAction>(root, "action", out _);
        if (action is null)
        {
            errors.Add($"'action' must be one of {SnakeCaseEnumConverter<PolicyAction>.NamesList}.");
        }

        var confidence = ReadConfidence(root, errors);
        var reason = ReadText(root, "reason", SuggestionOutputParser.MaxReasonLength);
        if (reason is null)
        {
            errors.Add("'reason' is missing.");
        }

        var topic = ReadLabel(root, "topicLabel", tree);
        if (topic.Invalid)
        {
            if (isMixed)
            {
                dropped.Add("'topicLabel' is not a valid label path; dropped (the sender is mixed).");
            }
            else
            {
                errors.Add("'topicLabel' is not a valid label path.");
            }
        }
        else if (topic.Label is null && !isMixed)
        {
            errors.Add("'topicLabel' is required unless the sender is mixed.");
        }

        var documentType = ReadDocumentType(root, topic.Label, tree, "", dropped);
        var mailType = ReadOptionalEnum<MailType>(root, "mailType", "", dropped);
        var retention = ReadRetention(root, "", dropped);

        if (isMixed && action is PolicyAction.Delete or PolicyAction.Unsubscribe)
        {
            dropped.Add($"A mixed sender's default cannot be '{Snake(action.Value)}'; changed to 'archive'.");
            action = PolicyAction.Archive;
        }

        if (profile.Stats.Allowlisted && action is { } given && given != PolicyAction.Keep)
        {
            dropped.Add($"The sender is allowlisted; action '{Snake(given)}' changed to 'keep'.");
            action = PolicyAction.Keep;
        }

        var rules = ReadRules(root, profile.Stats.Allowlisted, tree, dropped);
        if (errors.Count > 0)
        {
            return new ParsedPolicy(null, [], errors, dropped);
        }

        var policy = new SenderPolicyRow
        {
            Scope = profile.Scope,
            ScopeKey = profile.ScopeKey,
            DisplayName = profile.DisplayNames.FirstOrDefault(),
            IsMixed = isMixed,
            TopicLabel = topic.Label,
            DocumentTypeLabel = documentType,
            MailType = mailType,
            RetentionDays = retention,
            Action = action!.Value,
            Confidence = confidence,
            Reason = reason!,
            PromptVersion = SenderPolicyPromptBuilder.Version,
            Status = PolicyStatus.Proposed,
            Rules = rules,
        };

        // Backstop: whatever the parser let through must also pass the rules every stored policy keeps.
        foreach (var (field, messages) in PolicyValidation.Validate(policy))
        {
            errors.AddRange(messages.Select(m => $"'{field}': {m}"));
        }

        if (errors.Count > 0)
        {
            return new ParsedPolicy(null, [], errors, dropped);
        }

        var labels = new[] { policy.TopicLabel, policy.DocumentTypeLabel }
            .Concat(rules.SelectMany(r => new[] { r.TopicLabel, r.DocumentTypeLabel }));
        return new ParsedPolicy(policy, rules, errors, dropped)
        {
            NewLabels = [.. labels.OfType<string>().Where(l => !tree.Contains(l)).Distinct(StringComparer.OrdinalIgnoreCase)],
        };
    }

    private List<SenderPolicyRuleRow> ReadRules(JsonElement root, bool allowlisted, LabelTreeIndex tree, List<string> dropped)
    {
        var rules = new List<SenderPolicyRuleRow>();
        if (!root.TryGetProperty("rules", out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return rules;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            dropped.Add("'rules' is not an array; ignored.");
            return rules;
        }

        var number = 0;
        foreach (var item in array.EnumerateArray())
        {
            number++;
            if (rules.Count == MaxRules)
            {
                dropped.Add($"Rule {number}: more than {MaxRules} rules; dropped.");
            }
            else if (ReadRule(item, $"Rule {number}: ", allowlisted, tree, dropped) is { } rule)
            {
                rules.Add(rule);
            }
        }

        // The initial order is cheapest-first (#354); the user may reorder before approving.
        var ordered = PolicyMatcher.CostOrder(rules).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Position = i;
        }

        return ordered;
    }

    private SenderPolicyRuleRow? ReadRule(JsonElement item, string prefix, bool allowlisted, LabelTreeIndex tree, List<string> dropped)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            dropped.Add(prefix + "not an object; dropped.");
            return null;
        }

        var match = item.TryGetProperty("match", out var m) && m.ValueKind == JsonValueKind.Object ? ReadMatch(m) : new RuleMatch();
        if (match.IsEmpty)
        {
            dropped.Add(prefix + "'match' sets no field; dropped.");
            return null;
        }

        var topic = ReadLabel(item, "topicLabel", tree);
        if (topic.Label is null)
        {
            dropped.Add(prefix + "'topicLabel' is missing or not a valid label path; dropped.");
            return null;
        }

        if (ReadEnum<PolicyAction>(item, "action", out _) is not { } action)
        {
            dropped.Add(prefix + "'action' is missing or unknown; dropped.");
            return null;
        }

        if (action == PolicyAction.Delete && (guard.HasKeyword(match.SubjectTemplate) || guard.HasKeyword(match.SubjectContains)))
        {
            dropped.Add(prefix + "deletes mail whose subject names a transactional document; dropped.");
            return null;
        }

        if (allowlisted && action != PolicyAction.Keep)
        {
            dropped.Add(prefix + $"the sender is allowlisted; action '{Snake(action)}' changed to 'keep'.");
            action = PolicyAction.Keep;
        }

        return new SenderPolicyRuleRow
        {
            Name = ReadText(item, "name", MaxNameLength) ?? Describe(match),
            Match = match,
            TopicLabel = topic.Label,
            DocumentTypeLabel = ReadDocumentType(item, topic.Label, tree, prefix, dropped),
            MailType = ReadOptionalEnum<MailType>(item, "mailType", prefix, dropped),
            RetentionDays = ReadRetention(item, prefix, dropped),
            Action = action,
            Status = PolicyStatus.Proposed,
            Source = PolicyRuleSource.Llm,
            Reason = ReadText(item, "reason", SuggestionOutputParser.MaxReasonLength) ?? "",
        };
    }

    /// <summary>The usable match fields; a field of the wrong type or an over-long value counts as unset.</summary>
    private static RuleMatch ReadMatch(JsonElement m)
    {
        bool? Flag(string name) =>
            m.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

        return new RuleMatch
        {
            ListIdPresent = Flag("listIdPresent"),
            ListUnsubscribePresent = Flag("listUnsubscribePresent"),
            FromAddress = ReadText(m, "fromAddress", MaxMatchValueLength, clip: false),
            FromSubdomain = ReadText(m, "fromSubdomain", MaxMatchValueLength, clip: false),
            Category = ReadEnum<MessageCategory>(m, "category", out _),
            SubjectTemplate = ReadText(m, "subjectTemplate", MaxMatchValueLength, clip: false),
            SubjectContains = ReadText(m, "subjectContains", MaxMatchValueLength, clip: false),
        };
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
        var name = string.Join(", ", parts.OfType<string>());
        return name.Length <= MaxNameLength ? name : name[..(char.IsHighSurrogate(name[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength)];
    }

    /// <summary>The trimmed, respelled label; <c>Invalid</c> when a value was given but is not a usable topic label.</summary>
    private static (string? Label, bool Invalid) ReadLabel(JsonElement item, string name, LabelTreeIndex tree)
    {
        if (!item.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return (null, false);
        }

        var label = element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() : null;
        if (label is { Length: 0 })
        {
            return (null, false);
        }

        return label is null || !LabelPath.IsValid(label) || LabelPath.IsReserved(label) ? (null, true) : (tree.Respell(label), false);
    }

    private static string? ReadDocumentType(JsonElement item, string? topic, LabelTreeIndex tree, string prefix, List<string> dropped)
    {
        var (label, invalid) = ReadLabel(item, "documentTypeLabel", tree);
        if (invalid || (label is not null && string.Equals(label, topic, StringComparison.OrdinalIgnoreCase)))
        {
            dropped.Add(prefix + "'documentTypeLabel' is not a valid label path or equals 'topicLabel'; dropped.");
            return null;
        }

        return label;
    }

    private static TEnum? ReadEnum<TEnum>(JsonElement item, string name, out bool present) where TEnum : struct, Enum
    {
        present = item.TryGetProperty(name, out var element) && element.ValueKind != JsonValueKind.Null;
        return present && element.ValueKind == JsonValueKind.String
            && SnakeCaseEnumConverter<TEnum>.TryFromDb(element.GetString()!, out var value)
            ? value
            : null;
    }

    private static TEnum? ReadOptionalEnum<TEnum>(JsonElement item, string name, string prefix, List<string> dropped)
        where TEnum : struct, Enum
    {
        var value = ReadEnum<TEnum>(item, name, out var present);
        if (present && value is null)
        {
            dropped.Add(prefix + $"'{name}' is not one of {SnakeCaseEnumConverter<TEnum>.NamesList}; dropped.");
        }

        return value;
    }

    private static int? ReadRetention(JsonElement item, string prefix, List<string> dropped)
    {
        if (!item.TryGetProperty("retentionDays", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var days) && days > 0)
        {
            return days;
        }

        dropped.Add(prefix + "'retentionDays' is not a positive whole number; dropped.");
        return null;
    }

    private static double ReadConfidence(JsonElement root, List<string> errors)
    {
        if (root.TryGetProperty("confidence", out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out var value) && double.IsFinite(value)
            && value >= -SuggestionOutputParser.ConfidenceTolerance && value <= 1 + SuggestionOutputParser.ConfidenceTolerance)
        {
            return Math.Clamp(value, 0, 1);
        }

        errors.Add("'confidence' must be a number between 0 and 1.");
        return 0;
    }

    private static bool? ReadBool(JsonElement item, string name, string prefix, List<string> errors)
    {
        if (!item.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        errors.Add(prefix + $"'{name}' must be true or false.");
        return null;
    }

    /// <summary>
    /// A trimmed string without control characters, null when missing or blank. Longer than <paramref name="max"/>: cut
    /// when <paramref name="clip"/>, else null (a cut match value would match different mail).
    /// </summary>
    private static string? ReadText(JsonElement item, string name, int max, bool clip = true)
    {
        if (!item.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = string.Join(' ', (element.GetString() ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        text = new string([.. text.Where(c => !char.IsControl(c))]);
        if (text.Length == 0 || (text.Length > max && !clip))
        {
            return null;
        }

        return text.Length <= max ? text : text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)];
    }

    /// <summary>The first <c>{</c> that starts a valid JSON object, ignoring code fences and prose around it.</summary>
    private static JsonDocument? ReadFirstObject(string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        var candidates = 0;
        for (var i = 0; i < bytes.Length && candidates < MaxCandidates; i++)
        {
            if (bytes[i] != (byte)'{')
            {
                continue;
            }

            candidates++;
            var reader = new Utf8JsonReader(bytes.AsSpan(i), ReaderOptions);
            try
            {
                return JsonDocument.ParseValue(ref reader);
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static string Snake(PolicyAction action) => SnakeCaseEnumConverter<PolicyAction>.ToDb(action);
}
