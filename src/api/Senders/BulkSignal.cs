using System.Buffers;
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
    private static readonly SearchValues<char> KeywordEnd = SearchValues.Create(";( \t\r\n");

    public static MessageOrigin Of(MessageRow m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var hasListHeaders = !string.IsNullOrWhiteSpace(m.ListId) || !string.IsNullOrWhiteSpace(m.ListUnsubscribe);
        var precedence = Keyword(m.Precedence);
        var autoSubmitted = Keyword(m.AutoSubmitted);
        var bulk = (precedence is not null && BulkPrecedence.Contains(precedence))
            || (autoSubmitted is not null && !string.Equals(autoSubmitted, "no", StringComparison.OrdinalIgnoreCase))
            || hasListHeaders
            || m.Category is MessageCategory.Promotions or MessageCategory.Social;
        if (bulk)
        {
            return MessageOrigin.Bulk;
        }

        return m.ThreadReplied == true || m.Category == MessageCategory.Primary ? MessageOrigin.Human : MessageOrigin.Unknown;
    }

    /// <summary>The leading keyword of a header value; RFC 3834 §5 allows parameters (<c>; a=b</c>) and comments after it.</summary>
    private static string? Keyword(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.TrimStart();
        var end = trimmed.AsSpan().IndexOfAny(KeywordEnd);
        var keyword = end < 0 ? trimmed : trimmed[..end];
        return keyword.Length == 0 ? null : keyword;
    }

}
