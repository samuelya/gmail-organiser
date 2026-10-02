using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Review;

/// <summary>Label ids to add and remove for one message; <see cref="Note"/> says why the delete label was withheld.</summary>
public sealed record ActionPlan(IReadOnlyList<string> Add, IReadOnlyList<string> Remove, string? Note);

/// <summary>
/// Turns an approved suggestion into label changes (DESIGN §6.3 outcome table): topic label, plus the action label and
/// staying in the inbox when it needs action, plus the delete label when deletable; everything else leaves the inbox.
/// A protected message (§6.4) never gets the delete label, whichever way it was asked for. Pure.
/// </summary>
public static class ActionPlanner
{
    public const string InboxLabel = "INBOX";

    /// <summary>System labels that stay the user's: apply never adds or removes them.</summary>
    public static readonly IReadOnlySet<string> Untouched = new HashSet<string>(
        [MessageProtection.StarredLabel, MessageProtection.ImportantLabel, "UNREAD"], StringComparer.Ordinal);

    /// <param name="labelIds">Label path to Gmail label id (case-insensitive): the topic label, the action label when
    /// the suggestion needs action and the delete label when it is deletable and not protected.</param>
    /// <exception cref="KeyNotFoundException">A label the plan needs is missing from <paramref name="labelIds"/>.</exception>
    public static ActionPlan Plan(
        SuggestionRow suggestion, MessageRow message, IReadOnlyDictionary<string, string> labelIds, AppSettings settings, bool senderAllowlisted)
    {
        var protectedReason = MessageProtection.Reason(message, senderAllowlisted);
        var add = new List<string> { labelIds[suggestion.TopicLabel] };
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
        string[] remove = suggestion.NeedsAction ? [] : [InboxLabel];
        return new ActionPlan(
            [.. add.Distinct(StringComparer.Ordinal).Where(id => !current.Contains(id) && !Untouched.Contains(id))],
            [.. remove.Where(id => current.Contains(id) && !Untouched.Contains(id))],
            note);
    }
}
