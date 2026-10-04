using GmailOrganiser.Analysis;

namespace GmailOrganiser.Review;

/// <summary>
/// The document-type label a user sets in review (edit, apply to rest of sender): <c>null</c> leaves it unchanged,
/// <c>""</c> clears it, any other value must sit exactly one level under the document-type parent (DESIGN §6.3).
/// </summary>
public static class DocumentTypeEdit
{
    public const string Field = "documentTypeLabel";
    public const string OffMessage = "Document-type labels are off.";

    /// <summary>
    /// The requested value for <see cref="Apply"/>: null (unchanged), <c>""</c> (clear, also for blank) or the label
    /// with the parent as configured. Adds a <see cref="Field"/> error to <paramref name="errors"/> when the parent is
    /// off, the path is not one Gmail accepts, it is not exactly one level under the parent, or it is the topic label.
    /// </summary>
    public static string? Validate(string? requested, string? parent, string? topicLabel, Dictionary<string, string[]> errors)
    {
        if (requested is null)
        {
            return null;
        }

        var value = requested.Trim();
        if (value.Length == 0)
        {
            return "";
        }

        var prefix = parent + "/";
        string? error = parent is null ? OffMessage
            : !LabelResolver.IsValid(value) ? "Not a label path Gmail accepts."
            : !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || value.AsSpan(prefix.Length).Contains('/')
                ? $"Must be exactly one level under '{parent}'."
            : string.Equals(value, topicLabel?.Trim(), StringComparison.OrdinalIgnoreCase) ? "Must differ from the topic label."
            : null;
        if (error is not null)
        {
            errors[Field] = [error];
            return null;
        }

        return prefix + value[prefix.Length..];
    }

    /// <summary>
    /// Sets <paramref name="requested"/> (<see cref="Validate"/>d) on the suggestion; true when the label changed.
    /// <paramref name="isNew"/> is whether Gmail lacks it, null when unknown (a changed label then counts as new).
    /// </summary>
    public static bool Apply(SuggestionRow s, string? requested, bool? isNew)
    {
        if (requested is null)
        {
            return false;
        }

        var type = requested.Length == 0 ? null : requested;
        if (string.Equals(s.DocumentTypeLabel, type, StringComparison.Ordinal))
        {
            return false;
        }

        s.DocumentTypeIsNew = type is not null && (isNew ?? true);
        s.DocumentTypeLabel = type;
        return true;
    }
}
