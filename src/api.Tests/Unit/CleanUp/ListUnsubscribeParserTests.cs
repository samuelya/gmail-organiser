using GmailOrganiser.CleanUp.Unsubscribe;

namespace GmailOrganiser.Tests.Unit.CleanUp;

public sealed class ListUnsubscribeParserTests
{
    private const string OneClick = "List-Unsubscribe=One-Click";

    private static string[] Parse(string? header) => [.. ListUnsubscribeParser.Parse(header).Select(u => u.OriginalString)];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<>")]
    [InlineData("<not a url>, <ftp://example.com/u>, <javascript:alert(1)>, </relative/path>, <mailto:>")]
    public void Nothing_usable_parses_to_an_empty_list(string? header) => Parse(header).ShouldBeEmpty();

    [Fact]
    public void Angle_bracket_list_keeps_header_order() =>
        Parse("<mailto:u@example.com?subject=unsubscribe>, <https://example.com/u/1>")
            .ShouldBe(["mailto:u@example.com?subject=unsubscribe", "https://example.com/u/1"]);

    [Fact]
    public void Whitespace_and_folding_inside_and_between_entries_is_dropped() =>
        Parse("  < https://example.com/u/\r\n 1 >,\t<mailto:u@example.com>  ")
            .ShouldBe(["https://example.com/u/1", "mailto:u@example.com"]);

    [Fact]
    public void Plain_comma_separated_list_without_brackets_is_accepted() =>
        Parse("https://example.com/u/1, mailto:u@example.com").ShouldBe(["https://example.com/u/1", "mailto:u@example.com"]);

    [Fact]
    public void Duplicates_are_listed_once() =>
        Parse("<https://example.com/u/1>, <https://example.com/u/1>, <mailto:u@example.com>")
            .ShouldBe(["https://example.com/u/1", "mailto:u@example.com"]);

    [Fact]
    public void Malformed_entries_are_ignored_and_the_rest_kept() =>
        Parse("<ftp://example.com/x>, <::bad::>, <http://example.com/u/2>, <https://example.com/u/3")
            .ShouldBe(["http://example.com/u/2"]);

    [Fact]
    public void Https_with_the_post_header_is_one_click()
    {
        var option = Resolve("<mailto:u@example.com>, <http://example.com/h>, <https://example.com/s>", OneClick);

        option.ShouldBe(new UnsubscribeOption(UnsubscribeMethod.OneClick, "https://example.com/s"));
    }

    [Theory]
    [InlineData("list-unsubscribe=one-click")]
    [InlineData("  LIST-UNSUBSCRIBE=ONE-CLICK ")]
    public void Post_header_matches_case_insensitively(string post) =>
        Resolve("<https://example.com/s>", post)!.Method.ShouldBe(UnsubscribeMethod.OneClick);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("List-Unsubscribe=Other")]
    public void Https_without_the_post_header_is_a_link(string? post) =>
        Resolve("<http://example.com/h>, <https://example.com/s>", post)
            .ShouldBe(new UnsubscribeOption(UnsubscribeMethod.Link, "https://example.com/s"));

    [Fact]
    public void Http_is_a_link_even_with_the_post_header() =>
        Resolve("<mailto:u@example.com>, <http://example.com/h>", OneClick)
            .ShouldBe(new UnsubscribeOption(UnsubscribeMethod.Link, "http://example.com/h"));

    [Fact]
    public void Mailto_only_is_returned_unchanged_with_its_query() =>
        Resolve("<mailto:u@example.com?subject=unsubscribe%20me&body=please>", OneClick)
            .ShouldBe(new UnsubscribeOption(UnsubscribeMethod.Mailto, "mailto:u@example.com?subject=unsubscribe%20me&body=please"));

    [Fact]
    public void No_usable_uri_resolves_to_null() => Resolve("<ftp://example.com/x>", OneClick).ShouldBeNull();

    private static UnsubscribeOption? Resolve(string header, string? post) =>
        ListUnsubscribeParser.Resolve(ListUnsubscribeParser.Parse(header), post);
}
