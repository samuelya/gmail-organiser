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
/// (labelled phase) are removed when the message carries them: user labels only, never one the plan adds. Pure.
/// </summary>
public static class ActionPlanner
{
    public const string InboxLabel = "INBOX";

    /// <summary>System labels that stay the user's: apply never adds or removes them.</summary>
    public static readonly IReadOnlySet<string> Untouched = new HashSet<string>(
        [MessageProtection.StarredLabel, MessageProtection.ImportantLabel, "UNREAD"], StringComparer.Ordinal);

    /// <param name="labelIds">Label path to Gmail label id (case-insensitive): the topic label, the document-type label
    /// when it is a valid path (an invalid one is skipped), the action label when the suggestion needs action and the
    /// delete label when it is deletable and not protected.</param>
    /// <param name="removable">Ids of the personal labels Gmail has now; a replaced label not in it (deleted, or now
    /// the action or delete label) is skipped. Null removes no replaced label.</param>
    /// <exception cref="KeyNotFoundException">A label the plan needs is missing from <paramref name="labelIds"/>.</exception>
    public static ActionPlan Plan(
        SuggestionRow suggestion,
        MessageRow message,
        IReadOnlyDictionary<string, string> labelIds,
        AppSettings settings,
        Allowlist allowlist,
        IReadOnlySet<string>? removable = null)
    {
        var protectedReason = MessageProtection.Reason(message, allowlist, settings.Protection);
        var add = new List<string> { labelIds[suggestion.TopicLabel] };
        if (suggestion.DocumentTypeLabel is { } type && LabelResolver.IsValid(type))
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

        // The topic or action label may itself be the delete label; protection wins over any of them.
        if (protectedReason is not null && labelIds.TryGetValue(settings.DeleteLabelName, out var deleteId))
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
}
