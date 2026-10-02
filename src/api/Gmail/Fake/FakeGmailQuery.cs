namespace GmailOrganiser.Gmail.Fake;

/// <summary>
/// The subset of Gmail search the fake understands: <c>from:&lt;address&gt;</c>, <c>from:@&lt;domain&gt;</c> (subdomains
/// included), <c>in:inbox</c>, <c>in:spam</c>, <c>in:trash</c>, <c>label:&lt;id&gt;</c> and <c>has:attachment</c>, combined with AND. Anything else throws.
/// </summary>
public static class FakeGmailQuery
{
    public static Func<FakeMessage, bool> Parse(string? query)
    {
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var predicates = terms.Select(ParseTerm).ToList();
        return m => predicates.TrueForAll(p => p(m));
    }

    /// <summary>Whether the query names Spam or Trash, which Gmail then lists even with <c>includeSpamTrash=false</c>.</summary>
    public static bool NamesSpamOrTrash(string? query) =>
        (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Any(t => t is "in:spam" or "in:trash" or "label:spam" or "label:trash");

    private static Func<FakeMessage, bool> ParseTerm(string term)
    {
        var colon = term.IndexOf(':', StringComparison.Ordinal);
        var key = colon > 0 ? term[..colon].ToLowerInvariant() : "";
        var value = colon > 0 ? term[(colon + 1)..] : "";
        return (key, value.ToLowerInvariant()) switch
        {
            ("in", "inbox") => m => HasLabel(m, "INBOX"),
            ("in", "spam") => m => HasLabel(m, "SPAM"),
            ("in", "trash") => m => HasLabel(m, "TRASH"),
            ("has", "attachment") => m => m.HasAttachment,
            ("label", { Length: > 0 }) => m => HasLabel(m, value),
            ("from", ['@', .. var domain]) when domain.Length > 0 => m => FromDomain(m, domain),
            ("from", { Length: > 0 } address) when address.Contains('@', StringComparison.Ordinal) =>
                m => GmailMetadataMapper.ParseFrom(m.From).Address == address,
            _ => throw new ArgumentException($"The fake Gmail client does not support the search term '{term}'.", nameof(term)),
        };
    }

    private static bool HasLabel(FakeMessage m, string labelId) => m.LabelIds.Contains(labelId, StringComparer.OrdinalIgnoreCase);

    private static bool FromDomain(FakeMessage m, string domain)
    {
        var actual = GmailMetadataMapper.ParseFrom(m.From).Domain;
        return actual == domain || actual.EndsWith("." + domain, StringComparison.Ordinal);
    }
}
