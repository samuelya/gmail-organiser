using GmailOrganiser.Review;

namespace GmailOrganiser.Analysis;

/// <summary>Why <see cref="DocumentTypePath.Normalise"/> refused a value.</summary>
public enum DocumentTypePathError
{
    None,

    /// <summary>Not a label path Gmail accepts (<see cref="LabelResolver.IsValid"/>).</summary>
    InvalidPath,

    /// <summary>Not exactly one level under the document-type parent.</summary>
    NotOneLevel,

    /// <summary>The same label as the topic label.</summary>
    SameAsTopic,
}

/// <summary>
/// The document-type label rule (DESIGN §6.3) shared by the LLM output parser and the review edit, so the two cannot
/// drift: a label path Gmail accepts, exactly one level under the parent (case-insensitive), not the topic label.
/// </summary>
public static class DocumentTypePath
{
    /// <summary>
    /// <paramref name="value"/> (trimmed, not blank) with the parent as configured, or null with the reason in
    /// <paramref name="error"/>.
    /// </summary>
    public static string? Normalise(string value, string parent, string? topicLabel, out DocumentTypePathError error)
    {
        var prefix = parent + "/";
        error = !LabelResolver.IsValid(value) ? DocumentTypePathError.InvalidPath
            : !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || value.AsSpan(prefix.Length).Contains('/')
                ? DocumentTypePathError.NotOneLevel
            : string.Equals(value, topicLabel?.Trim(), StringComparison.OrdinalIgnoreCase) ? DocumentTypePathError.SameAsTopic
            : DocumentTypePathError.None;
        return error == DocumentTypePathError.None ? prefix + value[prefix.Length..] : null;
    }
}
