using System.Net;
using System.Text;

namespace GmailOrganiser.Analysis.Prompts;

/// <summary>
/// Turns a message body into short plain text for the prompt. A small forgiving html scanner (no regex on raw
/// html): unknown or broken markup is dropped, never thrown on.
/// </summary>
public static class BodyCleaner
{
    public const string TruncatedMarker = "[truncated]";

    private static readonly HashSet<string> SkippedElements = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "head" };

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "br", "dd", "div", "dl", "dt", "footer", "form", "h1", "h2", "h3",
        "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre", "section", "table", "td", "th", "tr", "ul",
    };

    public static string Clean(string? text, string? html, int maxChars)
    {
        maxChars = Math.Max(0, maxChars);
        var raw = !string.IsNullOrWhiteSpace(text) ? text : html is null ? string.Empty : HtmlToText(html, maxChars);
        return Truncate(Normalise(raw, maxChars), maxChars);
    }

    /// <summary>Converts until more than <paramref name="maxChars"/> visible characters are collected.</summary>
    private static string HtmlToText(string html, int maxChars)
    {
        var sb = new StringBuilder(Math.Min(html.Length, maxChars + 1));
        var visible = 0;
        var i = 0;
        while (i < html.Length && visible <= maxChars)
        {
            var lt = html.IndexOf('<', i);
            var end = lt < 0 ? html.Length : lt;
            var decoded = WebUtility.HtmlDecode(html[i..end]);
            sb.Append(decoded);
            visible += decoded.Count(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && !IsInvisible(c));
            i = lt < 0 ? html.Length : ReadMarkup(html, lt, sb);
        }

        return sb.ToString();
    }

    /// <summary>Consumes the markup at <paramref name="lt"/> and returns the index after it.</summary>
    private static int ReadMarkup(string html, int lt, StringBuilder sb)
    {
        if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
        {
            var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
            return end < 0 ? html.Length : end + 3;
        }

        var next = lt + 1 < html.Length ? html[lt + 1] : '\0';
        var closing = next == '/';
        var nameStart = closing ? lt + 2 : lt + 1;
        if (!closing && next is not ('!' or '?') && !char.IsAsciiLetter(next))
        {
            sb.Append('<');
            return lt + 1;
        }

        var nameEnd = nameStart;
        while (nameEnd < html.Length && char.IsAsciiLetterOrDigit(html[nameEnd]))
        {
            nameEnd++;
        }

        var name = html[nameStart..nameEnd];
        var tagEnd = FindTagEnd(html, nameEnd);
        if (tagEnd < 0)
        {
            return html.Length;
        }

        if (BlockElements.Contains(name))
        {
            sb.Append('\n');
        }

        if (!closing && SkippedElements.Contains(name) && html[tagEnd - 1] != '/')
        {
            return SkipElement(html, name, tagEnd + 1);
        }

        return tagEnd + 1;
    }

    /// <summary>
    /// Index after a skipped element's end tag. A <c>head</c> also ends at <c>&lt;body</c>; without an end, only the
    /// start tag is dropped, so a missing end tag never swallows the rest of the document.
    /// </summary>
    private static int SkipElement(string html, string name, int from)
    {
        var close = html.IndexOf("</" + name, from, StringComparison.OrdinalIgnoreCase);
        if (name.Equals("head", StringComparison.OrdinalIgnoreCase))
        {
            var body = html.IndexOf("<body", from, StringComparison.OrdinalIgnoreCase);
            if (body >= 0 && (close < 0 || body < close))
            {
                return body;
            }
        }

        if (close < 0)
        {
            return from;
        }

        var closeEnd = FindTagEnd(html, close + 2 + name.Length);
        return closeEnd < 0 ? html.Length : closeEnd + 1;
    }

    /// <summary>
    /// Index of the <c>&gt;</c> closing a tag, skipping attribute values quoted right after <c>=</c>; -1 when
    /// unterminated. A stray quote elsewhere is just a character.
    /// </summary>
    private static int FindTagEnd(string html, int from)
    {
        char? quote = null;
        var afterEquals = false;
        for (var i = from; i < html.Length; i++)
        {
            var c = html[i];
            if (quote is not null)
            {
                quote = c == quote ? null : quote;
            }
            else if (afterEquals && c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i;
            }

            afterEquals = quote is null && (c == '=' || (afterEquals && char.IsWhiteSpace(c)));
        }

        return -1;
    }

    private static bool IsInvisible(char c) => c is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF' or '\u00AD' or '\u034F';

    /// <summary>
    /// Drops zero-width characters, collapses blanks to single spaces and blank lines to single newlines; stops once
    /// the result is longer than <paramref name="maxChars"/>.
    /// </summary>
    private static string Normalise(string raw, int maxChars)
    {
        var sb = new StringBuilder(Math.Min(raw.Length, maxChars + 1));
        var pendingSpace = false;
        foreach (var c in raw)
        {
            if (sb.Length > maxChars)
            {
                break;
            }

            if (IsInvisible(c))
            {
                continue;
            }

            if (c is '\n' or '\r')
            {
                pendingSpace = false;
                if (sb.Length > 0 && sb[^1] != '\n')
                {
                    sb.Append('\n');
                }
            }
            else if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = sb.Length > 0 && sb[^1] != '\n';
            }
            else
            {
                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }

                sb.Append(c);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = maxChars > 0 && char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return text[..cut].TrimEnd() + " " + TruncatedMarker;
    }
}
