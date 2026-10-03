using System.Net;
using System.Net.Sockets;

namespace GmailOrganiser.CleanUp.Unsubscribe;

/// <summary>Why a one-click target was refused; <see cref="Allowed"/> when it was not.</summary>
public enum UnsubscribeTargetVerdict
{
    Allowed,
    NotHttps,
    NotPort443,
    HasUserInfo,
    IpLiteral,
    NoAddresses,
    NonPublicAddress,
}

/// <summary>
/// The SSRF guard for the one-click POST (epic #24 Q-X1): only <c>https</c> on port 443, no credentials in the URL,
/// a host name rather than an IP literal, and every address it resolves to public unicast. Runs on the addresses the
/// sender actually connects to (<see cref="HttpUnsubscribeSender"/>), so a name pointing at loopback, a private range or
/// link-local is never reached. Pure.
/// </summary>
public static class UnsubscribeTargetGuard
{
    /// <summary>The URL checks that need no DNS.</summary>
    public static UnsubscribeTargetVerdict ValidateUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            return UnsubscribeTargetVerdict.NotHttps;
        }

        if (url.Port != 443)
        {
            return UnsubscribeTargetVerdict.NotPort443;
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            return UnsubscribeTargetVerdict.HasUserInfo;
        }

        return url.HostNameType is UriHostNameType.Dns ? UnsubscribeTargetVerdict.Allowed : UnsubscribeTargetVerdict.IpLiteral;
    }

    /// <summary><see cref="ValidateUrl"/>, then every one of <paramref name="addresses"/> (the host's resolution) must be public.</summary>
    public static UnsubscribeTargetVerdict Validate(Uri url, IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var verdict = ValidateUrl(url);
        if (verdict != UnsubscribeTargetVerdict.Allowed)
        {
            return verdict;
        }

        if (addresses.Count == 0)
        {
            return UnsubscribeTargetVerdict.NoAddresses;
        }

        return addresses.All(IsPublicUnicast) ? UnsubscribeTargetVerdict.Allowed : UnsubscribeTargetVerdict.NonPublicAddress;
    }

    /// <summary>
    /// False for unspecified, loopback, private, shared (CGNAT), link-local, documentation, benchmarking, multicast and
    /// reserved IPv4; IPv6 must be global unicast (<c>2000::/3</c>) outside documentation and Teredo, and an
    /// IPv4-mapped, -compatible or 6to4 address is judged by the IPv4 address it carries.
    /// </summary>
    public static bool IsPublicUnicast(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicIPv4(address.MapToIPv4());
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIPv4(address),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(address),
            _ => false,
        };
    }

    private static bool IsPublicIPv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => false,
            100 => b[1] is < 64 or > 127,
            169 => b[1] != 254,
            172 => b[1] is < 16 or > 31,
            192 => !(b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2)),
            198 => b[1] is not (18 or 19) && !(b[1] == 51 && b[2] == 100),
            203 => !(b[1] == 0 && b[2] == 113),
            >= 224 => false,
            _ => true,
        };
    }

    private static bool IsPublicIPv6(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
        {
            // ::, ::1 and the deprecated IPv4-compatible ::a.b.c.d.
            return b.AsSpan(12).IndexOfAnyExcept((byte)0) >= 0 && !address.Equals(IPAddress.IPv6Loopback)
                && IsPublicIPv4(new IPAddress(b.AsSpan(12)));
        }

        if ((b[0] & 0xE0) != 0x20)
        {
            // Outside 2000::/3: link-local, unique local, site-local, multicast, NAT64 and the rest.
            return false;
        }

        return (b[0], b[1], b[2], b[3]) switch
        {
            (0x20, 0x01, 0x0D, 0xB8) => false,
            (0x20, 0x01, 0x00, 0x00) => false,
            (0x20, 0x02, _, _) => IsPublicIPv4(new IPAddress(b.AsSpan(2, 4))),
            _ => true,
        };
    }
}
