using System.Text.RegularExpressions;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Senders;

/// <summary>A sender address and its relay-decoded canonical form (DESIGN §6.1).</summary>
/// <param name="CanonicalAddress">The decoded address, or the address itself; lower-case.</param>
/// <param name="CanonicalDomain">The <see cref="SenderAddress.Domain"/> of <paramref name="CanonicalAddress"/>.</param>
/// <param name="IsRelay">Whether the address was a relay address that could be decoded.</param>
public readonly record struct CanonicalSender(string CanonicalAddress, string CanonicalDomain, bool IsRelay);

/// <summary>
/// Decodes Apple Hide-My-Email relay addresses (<c>news_at_mail_example_com_ab12cd_ef34gh@icloud.com</c>) back to the
/// address they stand for (<c>news@mail.example.com</c>), so one company's mail groups under one canonical sender.
/// The relay encodes both <c>.</c> and <c>_</c> as <c>_</c>, so the decoded <b>local part is lossy</b> (every <c>_</c>
/// in it stays <c>_</c>, and the domain's <c>_</c> all become <c>.</c>); the <b>domain</b> is what grouping relies on.
/// The local part ends at the <b>first</b> <c>_at_</c>, so a domain with an <c>at</c> label decodes whole, and the decoded
/// domain must have at least two labels (every real domain does), so a personal iCloud address with underscores
/// (<c>mike_at_work_01_a@icloud.com</c>) is not mistaken for a relay. A relay-domain address without the <c>_at_</c> form
/// (common for <c>privaterelay.appleid.com</c>) stays as it is.
/// </summary>
public static partial class RelayAddressDecoder
{
    /// <summary>iCloud's domain: relays use the <c>_at_</c> form; any other address here is a personal iCloud address.</summary>
    public const string ICloudDomain = "icloud.com";

    /// <summary>Sign in with Apple's relay domain: an address here without the <c>_at_</c> form is an opaque relay.</summary>
    public const string PrivateRelayDomain = "privaterelay.appleid.com";

    /// <summary>The relay platforms' domains; platform constants, not personal data.</summary>
    public static readonly string[] RelayDomains = [ICloudDomain, PrivateRelayDomain];

    public static CanonicalSender Decode(string address)
    {
        var sender = new SenderAddress(address.Trim().ToLowerInvariant(), null);
        var domain = sender.Domain;
        var localLength = sender.Address.Length - domain.Length - 1;
        if (localLength > 0 && RelayDomains.Contains(domain, StringComparer.Ordinal)
            && MatchRelay(sender.Address[..localLength]) is { Success: true } match)
        {
            var decoded = new SenderAddress($"{match.Groups["local"].Value}@{match.Groups["domain"].Value.Replace('_', '.')}", null);
            return new CanonicalSender(decoded.Address, decoded.Domain, true);
        }

        return new CanonicalSender(sender.Address, domain, false);
    }

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

    [GeneratedRegex("^(?<local>.+?)_at_(?<domain>[a-z0-9-]+(?:_[a-z0-9-]+)+)_[a-z0-9]+_[a-z0-9]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex RelayLocalPart();
}
