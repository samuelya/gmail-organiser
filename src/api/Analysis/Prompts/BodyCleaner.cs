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
        var raw = !string.IsNullOrWhiteSpace(text) ? text : html is null ? string.Empty : HtmlToText(html);
        return Truncate(Normalise(raw), Math.Max(0, maxChars));
    }

    private static string HtmlToText(string html)
    {
        var sb = new StringBuilder(html.Length);
        var i = 0;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                sb.Append(html, i, html.Length - i);
                break;
            }

            sb.Append(html, i, lt - i);
            i = ReadMarkup(html, lt, sb);
        }

        return WebUtility.HtmlDecode(sb.ToString());
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
            var close = html.IndexOf("</" + name, tagEnd + 1, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                return html.Length;
            }

            var closeEnd = FindTagEnd(html, close + 2 + name.Length);
            return closeEnd < 0 ? html.Length : closeEnd + 1;
        }

        return tagEnd + 1;
    }

    /// <summary>Index of the <c>&gt;</c> closing a tag, skipping quoted attribute values; -1 when unterminated.</summary>
    private static int FindTagEnd(string html, int from)
    {
        char? quote = null;
        for (var i = from; i < html.Length; i++)
        {
            var c = html[i];
            if (quote is not null)
            {
                quote = c == quote ? null : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Drops zero-width characters, collapses blanks to single spaces and blank lines to single newlines.</summary>
    private static string Normalise(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var c in raw)
        {
            if (c is '​' or '‌' or '‍' or '⁠' or '﻿' or '­' or '͏')
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
