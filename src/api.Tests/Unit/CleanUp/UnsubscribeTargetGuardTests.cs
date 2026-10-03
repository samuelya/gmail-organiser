using System.Net;
using GmailOrganiser.CleanUp.Unsubscribe;

namespace GmailOrganiser.Tests.Unit.CleanUp;

/// <summary>Uses documentation-style public addresses only as inputs; nothing is resolved or contacted.</summary>
public sealed class UnsubscribeTargetGuardTests
{
    private const string Public4 = "93.184.215.14";
    private const string Public6 = "2606:2800:21f:cb07::1";

    private static UnsubscribeTargetVerdict Validate(string url, params string[] addresses) =>
        UnsubscribeTargetGuard.Validate(new Uri(url), [.. addresses.Select(IPAddress.Parse)]);

    [Fact]
    public void Https_name_resolving_to_public_addresses_is_allowed()
    {
        Validate("https://example.com/u/1?t=x", Public4, Public6).ShouldBe(UnsubscribeTargetVerdict.Allowed);
        Validate("https://example.com:443/u", Public4).ShouldBe(UnsubscribeTargetVerdict.Allowed);
    }

    [Theory]
    [InlineData("http://example.com/u")]
    [InlineData("mailto:u@example.com")]
    [InlineData("ftp://example.com/u")]
    public void Anything_but_https_is_refused(string url) => Validate(url, Public4).ShouldBe(UnsubscribeTargetVerdict.NotHttps);

    [Theory]
    [InlineData("https://example.com:8443/u")]
    [InlineData("https://example.com:80/u")]
    public void Another_port_is_refused(string url) => Validate(url, Public4).ShouldBe(UnsubscribeTargetVerdict.NotPort443);

    [Fact]
    public void Credentials_in_the_url_are_refused() =>
        Validate("https://user:secret@example.com/u", Public4).ShouldBe(UnsubscribeTargetVerdict.HasUserInfo);

    [Theory]
    [InlineData("https://93.184.215.14/u")]
    [InlineData("https://127.0.0.1/u")]
    [InlineData("https://[2606:2800:21f:cb07::1]/u")]
    [InlineData("https://[::1]/u")]
    public void Ip_literal_hosts_are_refused_even_when_public(string url) =>
        Validate(url, Public4).ShouldBe(UnsubscribeTargetVerdict.IpLiteral);

    [Fact]
    public void A_name_without_addresses_is_refused() => Validate("https://example.com/u").ShouldBe(UnsubscribeTargetVerdict.NoAddresses);

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.8")]
    [InlineData("192.0.2.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::127.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001::1")]
    [InlineData("2002:c0a8:101::1")]
    public void Non_public_addresses_are_refused(string address)
    {
        UnsubscribeTargetGuard.IsPublicUnicast(IPAddress.Parse(address)).ShouldBeFalse();
        Validate("https://example.com/u", address).ShouldBe(UnsubscribeTargetVerdict.NonPublicAddress);
    }

    [Fact]
    public void One_non_public_address_among_public_ones_refuses_the_target() =>
        Validate("https://example.com/u", Public4, "127.0.0.1", Public6).ShouldBe(UnsubscribeTargetVerdict.NonPublicAddress);

    [Theory]
    [InlineData(Public4)]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("::ffff:93.184.215.14")]
    [InlineData(Public6)]
    [InlineData("2002:5db8:d70e::1")]
    public void Public_unicast_addresses_pass(string address) =>
        UnsubscribeTargetGuard.IsPublicUnicast(IPAddress.Parse(address)).ShouldBeTrue();
}
