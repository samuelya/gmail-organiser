using GmailOrganiser.Fetch;

namespace GmailOrganiser.Tests.Unit;

public sealed class AccountGuardMaskTests
{
    [Theory]
    [InlineData("user@example.com", "u***@example.com")]
    [InlineData("  Other.Name+tag@sub.example.com ", "O***@sub.example.com")]
    [InlineData("x@example.com", "x***@example.com")]
    [InlineData("@example.com", "***@example.com")]
    [InlineData("no-at-sign", "n***")]
    [InlineData("", "***")]
    public void Keeps_the_first_character_and_the_full_domain(string email, string expected) =>
        AccountGuard.Mask(email).ShouldBe(expected);
}
