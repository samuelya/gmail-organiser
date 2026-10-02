using GmailOrganiser.Fetch;

namespace GmailOrganiser.Analysis;

/// <summary>
/// The single protection rule (epic #22 safety rules): a protected message is always sent to the LLM and never gets a
/// derived or memory <c>toBeDeleted</c>. Replied-thread protection arrives in M5.
/// </summary>
public static class MessageProtection
{
    public const string StarredLabel = "STARRED";
    public const string ImportantLabel = "IMPORTANT";

    public static bool IsProtected(MessageRow m, bool senderAllowlisted) =>
        senderAllowlisted
        || m.HasAttachment
        || m.LabelIds.Contains(StarredLabel, StringComparer.Ordinal)
        || m.LabelIds.Contains(ImportantLabel, StringComparer.Ordinal);

    public static bool IsProtected(MessageRow m, IReadOnlySet<string> allowlistedSenders) =>
        IsProtected(m, allowlistedSenders.Contains(m.FromAddress));
}
