using GmailOrganiser.Senders;

namespace GmailOrganiser.Tests.Unit;

public sealed class RelayAddressDecoderTests
{
    [Theory]
    [InlineData("news_at_example_com_ab12cd_ef34gh@icloud.com", "news@example.com", "example.com")]
    [InlineData("billing_at_shop_example_com_x1y2_z3@privaterelay.appleid.com", "billing@shop.example.com", "shop.example.com")]
    [InlineData("news_at_mail_example_co_uk_ab12cd_ef34gh@icloud.com", "news@mail.example.co.uk", "mail.example.co.uk")]
    [InlineData("News_AT_Example_COM_AB12CD_EF34GH@iCloud.com", "news@example.com", "example.com")]
    [InlineData("no_reply_at_example_com_ab12cd_ef34gh@icloud.com", "no_reply@example.com", "example.com")]
    public void Relay_address_with_the_at_form_decodes_to_the_lower_case_original(string address, string canonical, string domain) =>
        RelayAddressDecoder.Decode(address).ShouldBe(new CanonicalSender(canonical, domain, true));

    [Theory]
    [InlineData("ab12cd34ef@privaterelay.appleid.com", "privaterelay.appleid.com")]
    [InlineData("person@icloud.com", "icloud.com")]
    [InlineData("news_at_example_com@icloud.com", "icloud.com")]
    [InlineData("news_at_example_com_ab-12_cd@icloud.com", "icloud.com")]
    public void Relay_domain_address_without_the_at_form_stays_as_it_is(string address, string domain) =>
        RelayAddressDecoder.Decode(address).ShouldBe(new CanonicalSender(address, domain, false));

    [Theory]
    [InlineData("Alice@Example.com", "alice@example.com", "example.com")]
    [InlineData("news_at_example_com_ab12cd_ef34gh@example.com", "news_at_example_com_ab12cd_ef34gh@example.com", "example.com")]
    [InlineData("news_at_example_com_ab12cd_ef34gh@mail.icloud.com", "news_at_example_com_ab12cd_ef34gh@mail.icloud.com", "mail.icloud.com")]
    [InlineData("not-an-address", "not-an-address", "")]
    public void Any_other_address_is_its_own_lower_case_canonical(string address, string canonical, string domain) =>
        RelayAddressDecoder.Decode(address).ShouldBe(new CanonicalSender(canonical, domain, false));
}
