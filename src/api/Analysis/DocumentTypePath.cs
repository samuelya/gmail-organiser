using GmailOrganiser.Gmail;
using GmailOrganiser.Review;

namespace GmailOrganiser.Analysis;

/// <summary>Why <see cref="DocumentTypePath.Normalise"/> refused a value.</summary>
public enum DocumentTypePathError
{
    None,

    /// <summary>Not a label path Gmail accepts (<see cref="LabelResolver.IsValid"/>).</summary>
    InvalidPath,

    /// <summary>Not 1 to <see cref="DocumentTypePath.MaxDepth"/> levels under the document-type parent.</summary>
    NotUnderParent,

    /// <summary>The same label as the topic label.</summary>
    SameAsTopic,
}

/// <summary>
/// The document-type label rule (DESIGN §6.3) shared by the LLM output parser and the review edit, so the two cannot
/// drift: a label path Gmail accepts, 1 to <see cref="MaxDepth"/> levels under the parent (case-insensitive), not the
/// topic label.
/// </summary>
public static class DocumentTypePath
{
    /// <summary>Most levels a document type may sit under the parent (general to specific).</summary>
    public const int MaxDepth = 3;

    /// <summary>Most existing document types <see cref="Children"/> lists (the prompt and Claude's label tree).</summary>
    public const int MaxChildren = 50;

    /// <summary>
    /// Most levels a type may sit under <paramref name="parent"/>: <see cref="MaxDepth"/>, fewer when the parent's own
    /// levels leave less room within Gmail's <see cref="GmailLimits.LabelMaxSegments"/>.
    /// </summary>
    public static int MaxDepthUnder(string parent) =>
        Math.Clamp(GmailLimits.LabelMaxSegments - (parent.AsSpan().Trim().Count('/') + 1), 1, MaxDepth);

    /// <summary><see cref="MaxDepthUnder"/> in words, "1 level" or "1 to n levels", for the prompt and error messages.</summary>
    public static string LevelsUnder(string parent)
    {
        var depth = MaxDepthUnder(parent);
        return depth == 1 ? "1 level" : $"1 to {depth} levels";
    }

    /// <summary>
    /// The existing document types: the labels 1 to <see cref="MaxDepth"/> levels under <paramref name="parent"/>
    /// (case-insensitive, deduplicated), at most <see cref="MaxChildren"/>, kept shallowest level first so a large
    /// subtree cannot push top-level types out, then in ordinal order so a type comes before its children; empty when
    /// the parent is off.
    /// </summary>
    public static IReadOnlyList<string> Children(string? parent, IEnumerable<string> labels) =>
        Children(parent, labels, out _);

    /// <inheritdoc cref="Children(string?, IEnumerable{string})"/>
    /// <param name="truncated">True when more than <see cref="MaxChildren"/> types exist.</param>
    public static IReadOnlyList<string> Children(string? parent, IEnumerable<string> labels, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(parent))
        {
            return [];
        }

        var prefix = parent.Trim() + "/";
        var all = labels
            .Where(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && IsDepthUnder(l, prefix.Length))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l.AsSpan(prefix.Length).Count('/'))
            .ThenBy(l => l, StringComparer.Ordinal)
            .Take(MaxChildren + 1)
            .ToList();
        truncated = all.Count > MaxChildren;
        return [.. all.Take(MaxChildren).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// <paramref name="value"/> (trimmed, not blank) with the parent as configured, or null with the reason in
    /// <paramref name="error"/>.
    /// </summary>
    public static string? Normalise(string value, string parent, string? topicLabel, out DocumentTypePathError error)
    {
        var prefix = parent + "/";
        error = !LabelResolver.IsValid(value) ? DocumentTypePathError.InvalidPath
            : !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !IsDepthUnder(value, prefix.Length)
                ? DocumentTypePathError.NotUnderParent
            : string.Equals(value, topicLabel?.Trim(), StringComparison.OrdinalIgnoreCase) ? DocumentTypePathError.SameAsTopic
            : DocumentTypePathError.None;
        return error == DocumentTypePathError.None ? prefix + value[prefix.Length..] : null;
    }

    // 1 to MaxDepth non-blank segments after the parent's prefix.
    private static bool IsDepthUnder(string label, int prefixLength)
    {
        var rest = label.AsSpan(prefixLength);
        if (rest.Count('/') >= MaxDepth)
        {
            return false;
        }

        foreach (var range in rest.Split('/'))
        {
            if (rest[range].IsWhiteSpace())
            {
                return false;
            }
        }

        return true;
    }
}
