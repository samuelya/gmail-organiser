using GmailOrganiser.Analysis;
using GmailOrganiser.Data;

namespace GmailOrganiser.Review;

/// <summary>A change to a suggestion's document-type label: <see cref="Unchanged"/>, cleared, or set to <see cref="Label"/>.</summary>
public readonly record struct DocumentTypeChange(bool IsSet, string? Label)
{
    public static readonly DocumentTypeChange Unchanged = default;
    public static readonly DocumentTypeChange Clear = new(true, null);

    public static DocumentTypeChange To(string label) => new(true, label);
}

/// <summary>A change to a suggestion's mail type (#365): unchanged (default), cleared, or set to <see cref="Value"/>.</summary>
public readonly record struct MailTypeChange(bool IsSet, MailType? Value)
{
    /// <summary>Null is unchanged, blank clears; anything else must name a <see cref="MailType"/> (an error under <c>mailType</c>).</summary>
    public static MailTypeChange Validate(string? value, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return default;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return new MailTypeChange(true, null);
        }

        if (SnakeCaseEnumConverter<MailType>.TryFromDb(value, out var type))
        {
            return new MailTypeChange(true, type);
        }

        errors["mailType"] = [$"Must be one of {SnakeCaseEnumConverter<MailType>.NamesList}."];
        return default;
    }
}

/// <summary>
/// The document-type label a user sets in review (edit, apply to rest of sender): on the wire <c>null</c> leaves it
/// unchanged, <c>""</c> clears it, any other value follows <see cref="DocumentTypePath"/> (DESIGN §6.3).
/// </summary>
public static class DocumentTypeEdit
{
    public const string Field = "documentTypeLabel";
    public const string OffMessage = "Document-type labels are off.";
    public const string SameAsTopicMessage = "Must differ from the topic label.";

    /// <summary>
    /// The requested change: null is <see cref="DocumentTypeChange.Unchanged"/>, blank clears, anything else is the
    /// label with the parent as configured. Adds a <see cref="Field"/> error to <paramref name="errors"/> when the parent
    /// is off or <see cref="DocumentTypePath.Normalise"/> refuses the value.
    /// </summary>
    public static DocumentTypeChange Validate(string? requested, string? parent, string? topicLabel, Dictionary<string, string[]> errors)
    {
        if (requested is null)
        {
            return DocumentTypeChange.Unchanged;
        }

        var value = requested.Trim();
        if (value.Length == 0)
        {
            return DocumentTypeChange.Clear;
        }

        if (parent is null)
        {
            errors[Field] = [OffMessage];
            return DocumentTypeChange.Unchanged;
        }

        if (DocumentTypePath.Normalise(value, parent, topicLabel, out var error) is { } label)
        {
            return DocumentTypeChange.To(label);
        }

        errors[Field] = [error switch
        {
            DocumentTypePathError.InvalidPath => "Not a label path Gmail accepts.",
            DocumentTypePathError.NotUnderParent => $"Must be {DocumentTypePath.LevelsUnder(parent)} under '{parent}'.",
            _ => SameAsTopicMessage,
        }];
        return DocumentTypeChange.Unchanged;
    }

    /// <summary>
    /// <paramref name="change"/> with the label in Gmail's spelling when Gmail has it (as analysis stores it, so group
    /// and sender-pattern matching agree), and whether Gmail lacks it: null when unknown (no connection) or not set.
    /// </summary>
    public static async Task<(DocumentTypeChange Change, bool? IsNew)> ResolveAsync(
        LabelCatalog labels, DocumentTypeChange change, CancellationToken ct)
    {
        if (change.Label is not { } label)
        {
            return (change, null);
        }

        try
        {
            return await labels.FindByNameAsync(label, ct) is { } found
                ? (DocumentTypeChange.To(found.Name.Trim()), false)
                : (change, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (change, null);
        }
    }

    /// <summary>Whether <paramref name="type"/> is the topic label (case-insensitive), which a document type must not be.</summary>
    public static bool IsTopic(string? type, string topicLabel) =>
        type is not null && string.Equals(type.Trim(), topicLabel.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sets <paramref name="change"/> (<see cref="Validate"/>d) on the suggestion. <paramref name="isNew"/> is whether
    /// Gmail lacks it, null when unknown (a changed label then counts as new).
    /// </summary>
    public static void Apply(SuggestionRow s, DocumentTypeChange change, bool? isNew)
    {
        if (!change.IsSet || string.Equals(s.DocumentTypeLabel, change.Label, StringComparison.Ordinal))
        {
            return;
        }

        s.DocumentTypeIsNew = change.Label is not null && (isNew ?? true);
        s.DocumentTypeLabel = change.Label;
    }
}
