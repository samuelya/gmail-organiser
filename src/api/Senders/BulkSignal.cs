using GmailOrganiser.Fetch;

namespace GmailOrganiser.Senders;

/// <summary>Whether a message was written by a person or sent in bulk, as far as its headers and category tell.</summary>
public enum MessageOrigin
{
    Human,
    Bulk,
    Unknown,
}

/// <summary>
/// The deterministic, per-message bulk-vs-human signal (DESIGN §6.4): <c>Precedence</c>, <c>Auto-Submitted</c>,
/// the list headers and the category tab. Pure; input for the per-sender aggregate and the policy matcher.
/// </summary>
public static class BulkSignal
{
    private static readonly HashSet<string> BulkPrecedence = new(StringComparer.OrdinalIgnoreCase) { "bulk", "list", "junk" };

    public static MessageOrigin Of(MessageRow m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var hasListHeaders = !string.IsNullOrWhiteSpace(m.ListId) || !string.IsNullOrWhiteSpace(m.ListUnsubscribe);
        var bulk = (m.Precedence is { } precedence && BulkPrecedence.Contains(precedence.Trim()))
            || (!string.IsNullOrWhiteSpace(m.AutoSubmitted) && !string.Equals(m.AutoSubmitted.Trim(), "no", StringComparison.OrdinalIgnoreCase))
            || hasListHeaders
            || m.Category is MessageCategory.Promotions or MessageCategory.Social;
        if (bulk)
        {
            return MessageOrigin.Bulk;
        }

        return m.ThreadReplied == true || m.Category == MessageCategory.Primary ? MessageOrigin.Human : MessageOrigin.Unknown;
    }
}
