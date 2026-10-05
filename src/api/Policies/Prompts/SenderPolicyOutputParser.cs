using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Settings;

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
public sealed partial class SenderPolicyOutputParser(TransactionalGuard guard)
{
    public const int MaxRules = 8;
    public const int MaxNameLength = 100;
    public const int MaxMatchValueLength = 200;

    private const string InvalidText = "Output contains a string that is not valid UTF-16 text.";
    private const string Ellipsis = "…";

    /// <param name="settings">The delete and action label names (never a topic or document-type label) and the
    /// document-type parent (null turns <c>documentTypeLabel</c> off).</param>
    public ParsedPolicy Parse(string? json, SenderProfile profile, LabelTreeIndex labelTree, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(labelTree);
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<string>();
        var dropped = new List<string>();
        using var document = SuggestionOutputParser.ReadFirstValue(json ?? "", out _);
        if (document?.RootElement.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Output is not a JSON object.");
            return new ParsedPolicy(null, [], errors, dropped);
        }

        try
        {
            return Read(document.RootElement, new Context(profile, labelTree, settings, dropped), errors);
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate escape (\ud800) in a name or string: System.Text.Json throws on reading it.
            errors.Add(InvalidText);
            return new ParsedPolicy(null, [], errors, dropped);
        }
    }

    private ParsedPolicy Read(JsonElement root, Context ctx, List<string> errors)
    {
        var (profile, tree, dropped) = (ctx.Profile, ctx.Tree, ctx.Dropped);
        var isMixed = ReadBool(root, "isMixed", errors) ?? false;
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

        var topic = ReadLabel(root, "topicLabel", ctx);
        if (topic.Invalid)
        {
            if (isMixed)
            {
                dropped.Add("'topicLabel' is not a valid label path or is a reserved label; dropped (the sender is mixed).");
            }
            else
            {
                errors.Add("'topicLabel' is not a valid label path or is a reserved label.");
            }
        }
        else if (topic.Label is null && !isMixed)
        {
            errors.Add("'topicLabel' is required unless the sender is mixed.");
        }

        var documentType = ReadDocumentType(root, topic.Label, ctx, "");
        var mailType = ReadOptionalEnum<MailType>(root, "mailType", "", dropped);
        var retention = ReadRetention(root, "", dropped);

        if (isMixed && action is PolicyAction.Delete or PolicyAction.Unsubscribe)
        {
            dropped.Add($"A mixed sender's default cannot be '{Snake(action.Value)}'; changed to 'archive'.");
            action = PolicyAction.Archive;
        }
        else if (action is PolicyAction.Delete or PolicyAction.Unsubscribe
            && profile.Templates.Any(t => guard.HasKeyword(t.Template)))
        {
            // Transactional mail is never deleted and never unsubscribed (DESIGN §6.2); the default covers every template.
            dropped.Add($"The sender's subjects name a transactional document; default '{Snake(action.Value)}' changed to 'archive'.");
            action = PolicyAction.Archive;
        }

        if (profile.Stats.Allowlisted && action is { } given && given != PolicyAction.Keep)
        {
            dropped.Add($"The sender is allowlisted; action '{Snake(given)}' changed to 'keep'.");
            action = PolicyAction.Keep;
        }

        var rules = isMixed ? ReadRules(root, ctx) : IgnoreRules(root, dropped);
        if (isMixed && rules.Count == 0)
        {
            // A mixed policy without rules matches nothing: approving it would never organise the sender.
            errors.Add("'rules': a mixed sender needs at least one usable rule.");
        }

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

    /// <summary>A sender that is not mixed has no rules (PolicyMatcher ignores them too); any given are noted.</summary>
    private static List<SenderPolicyRuleRow> IgnoreRules(JsonElement root, List<string> dropped)
    {
        if (root.TryGetProperty("rules", out var array) && array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0)
        {
            dropped.Add($"'rules': {array.GetArrayLength()} rule(s) ignored; the sender is not mixed.");
        }

        return [];
    }

    private List<SenderPolicyRuleRow> ReadRules(JsonElement root, Context ctx)
    {
        var rules = new List<SenderPolicyRuleRow>();
        if (!root.TryGetProperty("rules", out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return rules;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            ctx.Dropped.Add("'rules' is not an array; ignored.");
            return rules;
        }

        var number = 0;
        foreach (var item in array.EnumerateArray().Take(MaxRules))
        {
            number++;
            if (ReadRule(item, $"Rule {number}: ", ctx) is { } rule)
            {
                rules.Add(rule);
            }
        }

        if (array.GetArrayLength() > MaxRules)
        {
            ctx.Dropped.Add($"'rules': {array.GetArrayLength() - MaxRules} rule(s) beyond the first {MaxRules} dropped.");
        }

        // The model's order is kept: the prompt asks for specific rules before broad ones, and the first match wins.
        for (var i = 0; i < rules.Count; i++)
        {
            rules[i].Position = i;
        }

        return rules;
    }

    private SenderPolicyRuleRow? ReadRule(JsonElement item, string prefix, Context ctx)
    {
        var dropped = ctx.Dropped;
        if (item.ValueKind != JsonValueKind.Object)
        {
            dropped.Add(prefix + "not an object; dropped.");
            return null;
        }

        var match = new RuleMatch();
        if (item.TryGetProperty("match", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            if (ReadMatch(m, out var invalid) is not { } read)
            {
                // Dropping only the field would widen the rule.
                dropped.Add(prefix + $"'match.{invalid}' is not a usable value; dropped.");
                return null;
            }

            match = read;
        }

        if (match.IsEmpty)
        {
            dropped.Add(prefix + "'match' sets no field; dropped.");
            return null;
        }

        var topic = ReadLabel(item, "topicLabel", ctx);
        if (topic.Label is null)
        {
            dropped.Add(prefix + "'topicLabel' is missing, not a valid label path or a reserved label; dropped.");
            return null;
        }

        if (ReadEnum<PolicyAction>(item, "action", out _) is not { } action)
        {
            dropped.Add(prefix + "'action' is missing or unknown; dropped.");
            return null;
        }

        if (match.SubjectTemplate is { } given)
        {
            if (ResolveTemplate(given, ctx.Profile) is not { } template)
            {
                // Dropping only the field would widen the rule; a template that matches no subject would never fire.
                dropped.Add(prefix + "'subjectTemplate' is not one of the profile's templates; dropped.");
                return null;
            }

            match.SubjectTemplate = template;
        }

        // Transactional mail is never deleted and never unsubscribed (DESIGN §6.2); checked on the full template.
        if (action is PolicyAction.Delete or PolicyAction.Unsubscribe
            && (guard.HasKeyword(match.SubjectTemplate) || guard.HasKeyword(match.SubjectContains)))
        {
            dropped.Add(prefix + $"'{Snake(action)}' on mail whose subject names a transactional document; dropped.");
            return null;
        }

        if (ctx.Profile.Stats.Allowlisted && action != PolicyAction.Keep)
        {
            dropped.Add(prefix + $"the sender is allowlisted; action '{Snake(action)}' changed to 'keep'.");
            action = PolicyAction.Keep;
        }

        return new SenderPolicyRuleRow
        {
            Name = ReadText(item, "name", MaxNameLength) ?? Describe(match),
            Match = match,
            TopicLabel = topic.Label,
            DocumentTypeLabel = ReadDocumentType(item, topic.Label, ctx, prefix),
            MailType = ReadOptionalEnum<MailType>(item, "mailType", prefix, dropped),
            RetentionDays = ReadRetention(item, prefix, dropped),
            Action = action,
            Status = PolicyStatus.Proposed,
            Source = PolicyRuleSource.Llm,
            Reason = ReadText(item, "reason", SuggestionOutputParser.MaxReasonLength) ?? "",
        };
    }

    /// <summary>The trimmed, respelled label; <c>Invalid</c> when given but not a valid path, a system label, or the
    /// configured delete or action label (those come only from the action).</summary>
    private static (string? Label, bool Invalid) ReadLabel(JsonElement item, string name, Context ctx)
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

        return label is null || !LabelPath.IsValid(label) || LabelPath.IsReserved(label) || ctx.IsConfiguredLabel(label)
            ? (null, true)
            : (ctx.Tree.Respell(label), false);
    }

    /// <summary>
    /// A label 1 to <see cref="DocumentTypePath.MaxDepth"/> levels under the document-type parent, as
    /// <see cref="SuggestionOutputParser"/> reads it; anything else, or any value with the parent off, is dropped with a note.
    /// </summary>
    private static string? ReadDocumentType(JsonElement item, string? topic, Context ctx, string prefix)
    {
        if (!item.TryGetProperty("documentTypeLabel", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var value = element.ValueKind == JsonValueKind.String ? element.GetString()!.Trim() : null;
        if (value is { Length: 0 })
        {
            return null;
        }

        if (value is null || ctx.DocumentTypeParent is not { } parent || ctx.IsConfiguredLabel(value))
        {
            return Ignored(value is null ? "not a string" : ctx.DocumentTypeParent is null ? "document types are off" : "a reserved label");
        }

        return DocumentTypePath.Normalise(value, parent, topic, out var error) ?? Ignored(error switch
        {
            DocumentTypePathError.InvalidPath => "not a valid label path",
            DocumentTypePathError.NotUnderParent => $"not {DocumentTypePath.LevelsUnder(parent)} below the document-type parent",
            _ => "same as topicLabel",
        });

        string? Ignored(string reason)
        {
            ctx.Dropped.Add(prefix + $"'documentTypeLabel' ignored ({reason}).");
            return null;
        }
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

    private static bool? ReadBool(JsonElement item, string name, List<string> errors)
    {
        if (!item.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        errors.Add($"'{name}' must be true or false.");
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

        return SuggestionOutputParser.Cut(text, max);
    }

    private static string Snake(PolicyAction action) => SnakeCaseEnumConverter<PolicyAction>.ToDb(action);

    /// <summary>What one parse reads besides the JSON, and where it notes what it drops.</summary>
    private sealed record Context(SenderProfile Profile, LabelTreeIndex Tree, AppSettings Settings, List<string> Dropped)
    {
        public string? DocumentTypeParent { get; } =
            string.IsNullOrWhiteSpace(Settings.DocumentTypeParent) ? null : Settings.DocumentTypeParent.Trim();

        public bool IsConfiguredLabel(string label) =>
            string.Equals(label.Trim(), Settings.DeleteLabelName.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(label.Trim(), Settings.ActionLabelName.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
