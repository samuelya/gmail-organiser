using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace GmailOrganiser.Fetch;

/// <summary>
/// Validates the user's sender fetch target before it becomes a Gmail <c>from:</c> query. Only characters with no
/// meaning in Gmail search are accepted, so the query needs no escaping: no spaces, quotes, parentheses, braces or
/// operators can reach it.
/// </summary>
public static partial class SenderFetchTarget
{
    /// <summary>RFC 5321's limit for a forward path.</summary>
    public const int MaxAddressLength = 254;

    public const int MaxDomainLength = 253;

    public const int MaxLocalLength = 64;

    /// <summary>
    /// Trims and lower-cases <paramref name="input"/>; an address must be <c>local@domain</c>, a domain a hostname of at
    /// least two labels (ASCII letters, digits, <c>-</c> and <c>.</c>; IDNs in punycode), optionally written as Gmail
    /// does, <c>@domain</c>. A single label such as <c>com</c> would match far too much of the mailbox.
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out SenderFetchCursor? cursor)
    {
        cursor = null;
        var target = input?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        if (target[0] == '@')
        {
            target = target[1..];
        }

        var at = target.IndexOf('@', StringComparison.Ordinal);
        if (at < 0)
        {
            if (!IsHostname(target))
            {
                return false;
            }

            cursor = new SenderFetchCursor(target, SenderFetchKind.Domain, null, 0);
            return true;
        }

        if (target.Length > MaxAddressLength || at > MaxLocalLength || !LocalPart().IsMatch(target[..at]) || !IsHostname(target[(at + 1)..]))
        {
            return false;
        }

        cursor = new SenderFetchCursor(target, SenderFetchKind.Address, null, 0);
        return true;
    }

    private static bool IsHostname(string value) => value.Length <= MaxDomainLength && Hostname().IsMatch(value);

    // Two or more dot-separated labels of letters, digits and inner hyphens.
    [GeneratedRegex(@"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Hostname();

    // Dot-atom local part without the characters Gmail search gives a meaning ({ } for OR, quotes, parentheses).
    [GeneratedRegex(@"\A[a-z0-9!#$%&'*+/=?^_`|~-]+(?:\.[a-z0-9!#$%&'*+/=?^_`|~-]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPart();
}
