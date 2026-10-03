using GmailOrganiser.Fetch;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Analysis;

/// <summary>
/// The protection rule (epic #22 safety rules, user-configurable since #176): a protected message is always sent to the
/// LLM and never gets a derived or memory <c>toBeDeleted</c>. An allowlisted sender is protected whatever the rules say.
/// </summary>
public static class MessageProtection
{
    public const string StarredLabel = "STARRED";
    public const string ImportantLabel = "IMPORTANT";

    public static bool IsProtected(MessageRow m, bool senderAllowlisted, ProtectionSettings rules) =>
        Reason(m, senderAllowlisted, rules) is not null;

    public static bool IsProtected(MessageRow m, IReadOnlySet<string> allowlistedSenders, ProtectionSettings rules) =>
        IsProtected(m, allowlistedSenders.Contains(m.FromAddress), rules);

    /// <summary>Why <paramref name="m"/> is protected under <paramref name="rules"/>, or null when it is not.</summary>
    public static string? Reason(MessageRow m, bool senderAllowlisted, ProtectionSettings rules) =>
        senderAllowlisted ? "allowlisted sender"
        : rules.Attachments && m.HasAttachment ? "attachment"
        : rules.Starred && m.LabelIds.Contains(StarredLabel, StringComparer.Ordinal) ? "starred"
        : rules.Important && m.LabelIds.Contains(ImportantLabel, StringComparer.Ordinal) ? "important"
        : null;
}
