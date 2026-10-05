using System.Text.RegularExpressions;

namespace GmailOrganiser.Senders;

/// <summary>A sender address and its relay-decoded canonical form (DESIGN §6.1).</summary>
/// <param name="CanonicalAddress">The decoded address, or the address itself; lower-case.</param>
/// <param name="CanonicalDomain">The part of <paramref name="CanonicalAddress"/> after <c>@</c>.</param>
/// <param name="IsRelay">Whether the address was a relay address that could be decoded.</param>
public readonly record struct CanonicalSender(string CanonicalAddress, string CanonicalDomain, bool IsRelay);

/// <summary>
/// Decodes Apple Hide-My-Email relay addresses (<c>news_at_mail_example_com_ab12cd_ef34gh@icloud.com</c>) back to the
/// address they stand for (<c>news@mail.example.com</c>), so one company's mail groups under one canonical sender.
/// The relay encodes both <c>.</c> and <c>_</c> as <c>_</c>, so the decoded <b>local part is lossy</b> (every <c>_</c>
/// in it stays <c>_</c>, and the domain's <c>_</c> all become <c>.</c>); the <b>domain</b> is what grouping relies on.
/// A relay-domain address without the <c>_at_</c> form (common for <c>privaterelay.appleid.com</c>) stays as it is.
/// </summary>
public static partial class RelayAddressDecoder
{
    /// <summary>The relay platforms' domains; platform constants, not personal data.</summary>
    public static readonly string[] RelayDomains = ["icloud.com", "privaterelay.appleid.com"];

    public static CanonicalSender Decode(string address)
    {
        var lower = address.Trim().ToLowerInvariant();
        var at = lower.LastIndexOf('@');
        var domain = at < 0 ? "" : lower[(at + 1)..];
        if (at > 0 && RelayDomains.Contains(domain, StringComparer.Ordinal) && MatchRelay(lower[..at]) is { Success: true } match)
        {
            var decodedDomain = match.Groups["domain"].Value.Replace('_', '.');
            return new CanonicalSender($"{match.Groups["local"].Value}@{decodedDomain}", decodedDomain, true);
        }

        return new CanonicalSender(lower, domain, false);
    }

    /// <summary>Whether <paramref name="address"/> is at one of the <see cref="RelayDomains"/>, decodable or not.</summary>
    public static bool IsRelayDomain(string address) =>
        RelayDomains.Any(d => address.EndsWith("@" + d, StringComparison.OrdinalIgnoreCase));

    /// <summary>A pathological local part that times out is treated as not decodable, never as a fetch failure.</summary>
    private static Match? MatchRelay(string localPart)
    {
        try
        {
            return RelayLocalPart().Match(localPart);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    [GeneratedRegex("^(?<local>.+)_at_(?<domain>.+)_[a-z0-9]+_[a-z0-9]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex RelayLocalPart();
}
