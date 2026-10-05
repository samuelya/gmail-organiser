namespace GmailOrganiser.Analysis.Grouping;

/// <summary>One representative's validated LLM decision (an invalid output is passed as <c>null</c>).</summary>
/// <param name="ReplaceLabels">Current labels the answer replaces; null is none.</param>
/// <param name="DocumentTypeLabel">The document-type label; null is none.</param>
/// <param name="MailType">The mail type (#365); null is none.</param>
public sealed record RepresentativeOutput(
    string TopicLabel,
    bool NeedsAction,
    bool ToBeDeleted,
    bool UnsubscribeSuggested,
    double Confidence,
    IReadOnlyList<string>? ReplaceLabels = null,
    string? DocumentTypeLabel = null,
    MailType? MailType = null);

/// <summary>Whether a group's remaining members get a derived suggestion.</summary>
public abstract record Derivation;

/// <summary>All representatives agree; <see cref="Confidence"/> is already lowered by the penalty and within [0, 1].</summary>
public sealed record Agreed(
    string TopicLabel,
    bool NeedsAction,
    bool ToBeDeleted,
    bool UnsubscribeSuggested,
    double Confidence) : Derivation
{
    /// <summary>The replaced labels every representative named, ordinal-sorted.</summary>
    public IReadOnlyList<string> ReplaceLabels { get; init; } = [];

    /// <summary>
    /// The document-type label the representatives that named one agree on (as the first wrote it); null when any
    /// representative named none, so a derived member is assigned less rather than a type not every sample showed.
    /// </summary>
    public string? DocumentTypeLabel { get; init; }

    /// <summary>The mail type the representatives agree on; null when any named none, like <see cref="DocumentTypeLabel"/>.</summary>
    public MailType? MailType { get; init; }
}

/// <summary>Representatives disagree or one has no valid output: every remaining member goes to the LLM individually.</summary>
public sealed record Mixed : Derivation
{
    public static readonly Mixed Instance = new();
}

/// <summary>
/// Epic #22 safety rule: derive only when every representative (at least two) has a valid output and all agree on
/// all decision fields, the replaced labels (same set, ordinal) included. One invalid representative makes the group
/// mixed rather than deriving from a partial sample. A missing document-type label is no opinion: representatives that
/// name different types (case-insensitive) are mixed, and a group where only some name the type derives with none. The
/// mail type follows the same rule (#365).
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
        var types = valid.Select(o => Normalised(o.DocumentTypeLabel)).ToList();
        var agree = valid.All(o =>
            string.Equals(o.TopicLabel.Trim(), label, StringComparison.OrdinalIgnoreCase)
            && o.NeedsAction == first.NeedsAction
            && o.ToBeDeleted == first.ToBeDeleted
            && SameSet(o.ReplaceLabels, first.ReplaceLabels));
        var mailTypes = valid.Select(o => o.MailType).ToList();
        if (!agree || types.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
            || mailTypes.OfType<MailType>().Distinct().Count() > 1)
        {
            return Mixed.Instance;
        }

        var confidence = Math.Clamp(valid.Min(o => o.Confidence) - Math.Max(penalty, 0), 0, 1);
        return new Agreed(label, first.NeedsAction, first.ToBeDeleted, valid.Any(o => o.UnsubscribeSuggested), confidence)
        {
            ReplaceLabels = Set(first.ReplaceLabels),
            DocumentTypeLabel = types.Contains(null) ? null : types[0],
            MailType = mailTypes.Contains(null) ? null : mailTypes[0],
        };
    }

    private static string? Normalised(string? label) => string.IsNullOrWhiteSpace(label) ? null : label.Trim();

    private static bool SameSet(IReadOnlyList<string>? a, IReadOnlyList<string>? b) => Set(a).SequenceEqual(Set(b), StringComparer.Ordinal);

    private static string[] Set(IReadOnlyList<string>? labels) =>
        [.. (labels ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
