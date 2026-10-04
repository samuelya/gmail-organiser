using GmailOrganiser.Analysis;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit;

/// <summary>The sender and domain allowlist (#203): matching and validation of <c>protection.allowlistedDomains</c>.</summary>
public sealed class AllowlistTests
{
    private static readonly Allowlist List = new(new HashSet<string> { "boss@example.org" }, ["example.com", "bank.example.net"]);

    [Theory]
    [InlineData("boss@example.org", Allowlist.SenderReason)]
    [InlineData("someone@example.com", Allowlist.DomainReason)]
    [InlineData("alerts@mail.example.com", Allowlist.DomainReason)]
    [InlineData("a@deep.sub.example.com", Allowlist.DomainReason)]
    [InlineData("statements@bank.example.net", Allowlist.DomainReason)]
    [InlineData("shop@example.net", null)]
    [InlineData("someone@notexample.com", null)]
    [InlineData("someone@example.com.example.org", null)]
    [InlineData("other@example.org", null)]
    [InlineData("no-domain", null)]
    public void Reason_matches_the_exact_address_then_the_domain_or_a_parent_domain(string address, string? expected)
    {
        List.Reason(address).ShouldBe(expected);
    }

    [Fact]
    public void The_address_wins_over_its_domain_and_the_empty_list_matches_nothing()
    {
        new Allowlist(new HashSet<string> { "a@example.com" }, ["example.com"]).Reason("a@example.com").ShouldBe(Allowlist.SenderReason);
        Allowlist.Empty.Reason("a@example.com").ShouldBeNull();
    }

    [Fact]
    public void The_domain_is_the_part_after_the_last_at_sign()
    {
        List.Reason("\"a@example.org\"@mail.example.com").ShouldBe(Allowlist.DomainReason);
        List.Reason("\"a@example.com\"@example.org").ShouldBeNull();
    }

    private static Dictionary<string, string[]> Validate(params string?[] domains) =>
        SettingsValidation.Validate(new UpdateSettingsRequest(
            null, null, null, null, Protection: new UpdateProtectionSettingsRequest(AllowlistedDomains: domains)));

    [Fact]
    public void Valid_domains_pass_and_are_normalised_without_duplicates()
    {
        Validate(" Example.COM ", "mail.example.org", "example.com", "example.net.", "bücher.example").ShouldBeEmpty();
        Validate().ShouldBeEmpty();
        SettingsValidation.NormaliseDomains([" Example.COM ", "mail.example.org", "example.com"])
            .ShouldBe(["example.com", "mail.example.org"]);
    }

    [Fact]
    public void A_trailing_dot_or_an_IDN_is_stored_as_a_stored_address_carries_the_domain()
    {
        SettingsValidation.NormaliseDomains(["Example.com.", "Bücher.Example", "xn--bcher-kva.example"])
            .ShouldBe(["example.com", "xn--bcher-kva.example"]);

        var allowlist = new Allowlist(new HashSet<string>(), SettingsValidation.NormaliseDomains(["example.com.", "bücher.example"]));
        allowlist.Reason("a@example.com").ShouldBe(Allowlist.DomainReason);
        allowlist.Reason("a@shop.xn--bcher-kva.example").ShouldBe(Allowlist.DomainReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("user@example.com")]
    [InlineData("@example.com")]
    [InlineData("https://example.com")]
    [InlineData("example.com/path")]
    [InlineData("exa mple.com")]
    [InlineData("192.0.2.1")]
    [InlineData("-example.com")]
    [InlineData("com")]
    [InlineData("localhost")]
    [InlineData("example.com..")]
    [InlineData(".")]
    [InlineData(null)]
    public void An_entry_that_is_not_a_domain_is_a_field_error_naming_it(string? domain)
    {
        var errors = Validate("example.com", domain);

        errors.Keys.ShouldBe([SettingsValidation.AllowlistedDomainsField]);
        errors[SettingsValidation.AllowlistedDomainsField].Single().ShouldContain($"'{domain ?? "null"}'");
    }

    [Fact]
    public void Too_long_or_too_many_domains_are_a_field_error()
    {
        var label = new string('a', 63);
        var tooLong = string.Join('.', label, label, label, label) + ".com";
        Validate(tooLong).Keys.ShouldBe([SettingsValidation.AllowlistedDomainsField]);

        var many = Enumerable.Range(0, SettingsValidation.MaxAllowlistedDomains).Select(i => $"d{i}.example.com").ToArray();
        Validate(many).ShouldBeEmpty();
        Validate([.. many, .. many]).ShouldBeEmpty();
        Validate([.. many, "extra.example.com"]).Keys.ShouldBe([SettingsValidation.AllowlistedDomainsField]);
    }
}
