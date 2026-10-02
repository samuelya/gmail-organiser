namespace GmailOrganiser.Analysis.Grouping;

/// <summary>One representative's validated LLM decision (an invalid output is passed as <c>null</c>).</summary>
public sealed record RepresentativeOutput(
    string TopicLabel,
    bool NeedsAction,
    bool ToBeDeleted,
    bool UnsubscribeSuggested,
    double Confidence);

/// <summary>Whether a group's remaining members get a derived suggestion.</summary>
public abstract record Derivation;

/// <summary>All representatives agree; <see cref="Confidence"/> is already lowered by the penalty and within [0, 1].</summary>
public sealed record Agreed(
    string TopicLabel,
    bool NeedsAction,
    bool ToBeDeleted,
    bool UnsubscribeSuggested,
    double Confidence) : Derivation;

/// <summary>Representatives disagree or one has no valid output: every remaining member goes to the LLM individually.</summary>
public sealed record Mixed : Derivation
{
    public static readonly Mixed Instance = new();
}

/// <summary>
/// Epic #22 safety rule: derive only when every representative (at least two) has a valid output and all agree on
/// all decision fields. One invalid representative makes the group mixed rather than deriving from a partial sample.
/// </summary>
public static class DerivationRule
{
    public const int MinValidRepresentatives = 2;

    public static Derivation Decide(IReadOnlyList<RepresentativeOutput?> representativeOutputs, double penalty)
    {
        var valid = representativeOutputs
            .OfType<RepresentativeOutput>()
            .Where(o => !string.IsNullOrWhiteSpace(o.TopicLabel) && double.IsFinite(o.Confidence))
            .ToList();
        if (valid.Count < MinValidRepresentatives || valid.Count < representativeOutputs.Count)
        {
            return Mixed.Instance;
        }

        var first = valid[0];
        var label = first.TopicLabel.Trim();
        var agree = valid.All(o =>
            string.Equals(o.TopicLabel.Trim(), label, StringComparison.OrdinalIgnoreCase)
            && o.NeedsAction == first.NeedsAction
            && o.ToBeDeleted == first.ToBeDeleted);
        if (!agree)
        {
            return Mixed.Instance;
        }

        var confidence = Math.Clamp(valid.Min(o => o.Confidence) - Math.Max(penalty, 0), 0, 1);
        return new Agreed(label, first.NeedsAction, first.ToBeDeleted, valid.Any(o => o.UnsubscribeSuggested), confidence);
    }
}
