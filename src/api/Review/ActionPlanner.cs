using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Review;

/// <summary>Label ids to add and remove for one message; <see cref="Note"/> says why the delete label was withheld.</summary>
public sealed record ActionPlan(IReadOnlyList<string> Add, IReadOnlyList<string> Remove, string? Note);

/// <summary>
/// Turns an approved suggestion into label changes (DESIGN §6.3 outcome table): topic and document-type label, plus
/// the action label and staying in the inbox when it needs action, plus the delete label when deletable; everything
/// else leaves the inbox.
/// A protected message (§6.4) never gets the delete label, whichever way it was asked for. The replaced labels
/// (labelled phase, or an applied outcome an accepted alternative replaces) are removed when the message carries them:
/// user labels only, never one the plan adds. A protected Stage-0 suggestion (its topic is the delete label) changes
/// nothing: the message stays where it is rather than leave the inbox unlabelled. The delete label is only ever added
/// to a deletable suggestion: one not to be deleted whose topic is the delete label changes nothing either. Pure.
/// </summary>
public static class ActionPlanner
{
    public const string InboxLabel = "INBOX";

    /// <summary>The note of a suggestion not to be deleted whose topic is the delete label: nothing is changed.</summary>
    public const string NotDeletableNote = "not deletable: the topic is the delete label";

    /// <summary>Why an edit or Claude's alternative may not make the delete label the topic of an email not to be deleted.</summary>
    public const string DeleteTopicError = "The delete label is only the topic of an email to be deleted.";

    /// <summary>Whether <paramref name="topic"/> is the delete label (trimmed, any case) on an email not to be deleted.</summary>
    public static bool IsDeleteTopicNotDeleted(string? topic, bool toBeDeleted, string deleteLabelName) =>
        !toBeDeleted && string.Equals(topic?.Trim(), deleteLabelName.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>System labels that stay the user's: apply never adds or removes them.</summary>
    public static readonly IReadOnlySet<string> Untouched = new HashSet<string>(
        [MessageProtection.StarredLabel, MessageProtection.ImportantLabel, "UNREAD"], StringComparer.Ordinal);

    /// <param name="labelIds">Label path to Gmail label id (case-insensitive): the topic label, the document-type label
    /// when <see cref="AppliesDocumentType"/> (otherwise it is skipped), the action label when the suggestion needs
    /// action and the delete label when it is deletable and not protected.</param>
    /// <param name="removable">Ids of the user labels Gmail has now (the action and delete label included: an accepted
    /// alternative replaces them); a replaced label not in it (deleted) is skipped. Null removes no replaced label.</param>
    /// <exception cref="KeyNotFoundException">A label the plan needs is missing from <paramref name="labelIds"/>.</exception>
    public static ActionPlan Plan(
        SuggestionRow suggestion,
        MessageRow message,
        IReadOnlyDictionary<string, string> labelIds,
        AppSettings settings,
        Allowlist allowlist,
        IReadOnlySet<string>? removable = null)
    {
        var topicIsDelete = string.Equals(suggestion.TopicLabel, settings.DeleteLabelName, StringComparison.OrdinalIgnoreCase);
        if (topicIsDelete && !suggestion.ToBeDeleted)
        {
            return new ActionPlan([], [], NotDeletableNote);
        }

        var protectedReason = MessageProtection.Reason(message, allowlist, settings.Protection);
        // An unedited Stage-0 delete card (or one still filed under the delete label) has nothing else to add: a
        // protected message stays put. An edited card with a real topic follows the normal path below.
        var stage0Delete = suggestion.Source == SuggestionSource.Stage0
            && ((suggestion.ToBeDeleted && !suggestion.Edited) || topicIsDelete);
        if (stage0Delete && protectedReason is not null)
        {
            return new ActionPlan([], [], $"protected: {protectedReason}");
        }

        var add = new List<string> { labelIds[suggestion.TopicLabel] };
        if (suggestion.DocumentTypeLabel is { } type && AppliesDocumentType(type, settings))
        {
            add.Add(labelIds[type]);
        }

        if (suggestion.NeedsAction)
        {
            add.Add(labelIds[settings.ActionLabelName]);
        }

        string? note = null;
        if (suggestion.ToBeDeleted && protectedReason is not null)
        {
            note = $"protected: {protectedReason}";
        }
        else if (suggestion.ToBeDeleted)
        {
            add.Add(labelIds[settings.DeleteLabelName]);
        }

        // The topic or action label may itself be the delete label; protection and not being deletable win over them.
        if ((protectedReason is not null || !suggestion.ToBeDeleted) && labelIds.TryGetValue(settings.DeleteLabelName, out var deleteId))
        {
            add.RemoveAll(id => id == deleteId);
        }

        var current = new HashSet<string>(message.LabelIds, StringComparer.Ordinal);
        var replaced = suggestion.ReplaceLabelIds
            .Where(id => removable?.Contains(id) == true && GmailLabelIds.IsUser(id) && !add.Contains(id, StringComparer.Ordinal));
        List<string> remove = [.. replaced];
        if (!suggestion.NeedsAction)
        {
            remove.Add(InboxLabel);
        }

        return new ActionPlan(
            [.. add.Distinct(StringComparer.Ordinal).Where(id => !current.Contains(id) && !Untouched.Contains(id))],
            [.. remove.Distinct(StringComparer.Ordinal).Where(id => current.Contains(id) && !Untouched.Contains(id))],
            note);
    }

    /// <summary>
    /// Whether a stored document-type label is applied: a path Gmail accepts that is not the current action or delete
    /// label. It was approved under the parent of its time, and settings only keep those two apart from the current one.
    /// </summary>
    public static bool AppliesDocumentType(string type, AppSettings settings) =>
        LabelResolver.IsValid(type)
        && !string.Equals(type, settings.ActionLabelName.Trim(), StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, settings.DeleteLabelName.Trim(), StringComparison.OrdinalIgnoreCase);
}
