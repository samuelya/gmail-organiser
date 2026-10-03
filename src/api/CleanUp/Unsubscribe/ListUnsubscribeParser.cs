namespace GmailOrganiser.CleanUp.Unsubscribe;

/// <summary>An unsubscribe target resolved from a message's headers.</summary>
/// <param name="Url">The URL as the sender wrote it (whitespace removed); a <c>mailto:</c> keeps its query.</param>
public sealed record UnsubscribeOption(UnsubscribeMethod Method, string Url);

/// <summary>
/// Parses <c>List-Unsubscribe</c> (RFC 2369: a comma-separated list of <c>&lt;uri&gt;</c>) and picks the method
/// (RFC 8058 one-click, then a link, then <c>mailto:</c>). Pure.
/// </summary>
public static class ListUnsubscribeParser
{
    /// <summary>The only <c>List-Unsubscribe-Post</c> value RFC 8058 defines; also the body of the one-click POST.</summary>
    public const string OneClickPostValue = "List-Unsubscribe=One-Click";

    /// <summary>
    /// The https, http and <c>mailto:</c> URIs in <paramref name="header"/>, in header order, without duplicates.
    /// Entries in angle brackets win; a header without any is read as plain comma-separated URIs. Other schemes,
    /// relative or malformed entries are ignored.
    /// </summary>
    public static IReadOnlyList<Uri> Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return [];
        }

        var uris = new List<Uri>();
        foreach (var entry in Entries(header))
        {
            // RFC 2369 lets a URI be folded across lines; whitespace is never part of it.
            var value = string.Concat(entry.Where(c => !char.IsWhiteSpace(c)));
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsSupported(uri)
                && !uris.Exists(u => string.Equals(u.OriginalString, uri.OriginalString, StringComparison.Ordinal)))
            {
                uris.Add(uri);
            }
        }

        return uris;
    }

    /// <summary>
    /// <see cref="UnsubscribeMethod.OneClick"/> when there is an https URI and <paramref name="listUnsubscribePost"/>
    /// is <see cref="OneClickPostValue"/>; else <see cref="UnsubscribeMethod.Link"/> (https first, then http); else
    /// <see cref="UnsubscribeMethod.Mailto"/>; null when <paramref name="uris"/> has none of them.
    /// </summary>
    public static UnsubscribeOption? Resolve(IReadOnlyList<Uri> uris, string? listUnsubscribePost)
    {
        ArgumentNullException.ThrowIfNull(uris);
        var https = uris.FirstOrDefault(u => u.Scheme == Uri.UriSchemeHttps);
        if (https is not null && IsOneClick(listUnsubscribePost))
        {
            return new UnsubscribeOption(UnsubscribeMethod.OneClick, https.OriginalString);
        }

        if ((https ?? uris.FirstOrDefault(u => u.Scheme == Uri.UriSchemeHttp)) is { } link)
        {
            return new UnsubscribeOption(UnsubscribeMethod.Link, link.OriginalString);
        }

        return uris.FirstOrDefault(u => u.Scheme == Uri.UriSchemeMailto) is { } mailto
            ? new UnsubscribeOption(UnsubscribeMethod.Mailto, mailto.OriginalString)
            : null;
    }

    public static bool IsOneClick(string? listUnsubscribePost) =>
        string.Equals(listUnsubscribePost?.Trim(), OneClickPostValue, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Entries(string header)
    {
        if (!header.Contains('<', StringComparison.Ordinal))
        {
            foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return part;
            }

            yield break;
        }

        var start = header.IndexOf('<', StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = header.IndexOf('>', start + 1);
            if (end < 0)
            {
                yield break;
            }

            yield return header[(start + 1)..end];
            start = header.IndexOf('<', end + 1);
        }
    }

    private static bool IsSupported(Uri uri) => uri.Scheme switch
    {
        "https" or "http" => !string.IsNullOrEmpty(uri.Host),
        "mailto" => uri.OriginalString.Length > "mailto:".Length && uri.OriginalString.Contains('@', StringComparison.Ordinal),
        _ => false,
    };
}
