using GmailOrganiser.Gmail;

namespace GmailOrganiser.Policies;

/// <summary>The rules every stored sender policy keeps, shared by the LLM output parser and the review edit.</summary>
public static class PolicyValidation
{
    public const int MaxRules = 12;

    /// <summary>The errors by field, as for a validation ProblemDetails; empty when the policy is valid.</summary>
    public static Dictionary<string, string[]> Validate(SenderPolicyRow policy)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                errors[field] = list = [];
            }

            list.Add(message);
        }

        if (policy.IsMixed && policy.Action == PolicyAction.Delete)
        {
            Add("action", "A mixed sender's default cannot delete; delete only through a rule.");
        }

        if (!policy.IsMixed && string.IsNullOrWhiteSpace(policy.TopicLabel))
        {
            Add("topicLabel", "Required unless the sender is mixed.");
        }

        CheckLabel(policy.TopicLabel, "topicLabel", Add);
        CheckLabel(policy.DocumentTypeLabel, "documentTypeLabel", Add);

        if (policy.Rules.Count > MaxRules)
        {
            Add("rules", $"At most {MaxRules} rules.");
        }

        for (var i = 0; i < policy.Rules.Count; i++)
        {
            var rule = policy.Rules[i];
            if (rule.Match.IsEmpty)
            {
                Add($"rules[{i}].match", "Set at least one match field.");
            }

            if (string.IsNullOrWhiteSpace(rule.TopicLabel))
            {
                Add($"rules[{i}].topicLabel", "Required.");
            }

            CheckLabel(rule.TopicLabel, $"rules[{i}].topicLabel", Add);
            CheckLabel(rule.DocumentTypeLabel, $"rules[{i}].documentTypeLabel", Add);
        }

        return errors.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
    }

    private static void CheckLabel(string? label, string field, Action<string, string> add)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        if (label.Length > GmailLimits.LabelNameMaxLength)
        {
            add(field, $"Must be at most {GmailLimits.LabelNameMaxLength} characters.");
        }

        if (label.Split('/').Length > GmailLimits.LabelMaxSegments)
        {
            add(field, $"Must be at most {GmailLimits.LabelMaxSegments} levels deep.");
        }
    }
}
