using GmailOrganiser.Analysis.Prompts;

namespace GmailOrganiser.Tests.Unit.Analysis;

public sealed class BodyCleanerTests
{
    [Fact]
    public void Text_is_preferred_over_html()
    {
        BodyCleaner.Clean("Plain  text\r\n\r\n\r\nsecond   line", "<p>Html</p>", 4000).ShouldBe("Plain text\nsecond line");
    }

    [Fact]
    public void Text_only_is_cleaned()
    {
        BodyCleaner.Clean("  Hello​ wor­ld \t!  ", null, 4000).ShouldBe("Hello world !");
    }

    [Fact]
    public void Html_only_drops_head_script_style_and_breaks_blocks()
    {
        const string html = """
            <html><head><title>Hidden</title><style>p { color: red; }</style></head>
            <body><script type="text/javascript">alert("x > y");</script>
            <div>First <b>bold</b> line</div><p>Second<br/>Third</p><!-- note <p>gone</p> -->
            <ul><li>One</li><li>Two</li></ul></body></html>
            """;

        BodyCleaner.Clean(null, html, 4000).ShouldBe("First bold line\nSecond\nThird\nOne\nTwo");
    }

    [Fact]
    public void Blank_text_falls_back_to_html()
    {
        BodyCleaner.Clean("  \n ", "<p>From html</p>", 4000).ShouldBe("From html");
    }

    [Fact]
    public void Entities_are_decoded()
    {
        BodyCleaner.Clean(null, "<p>Caf&eacute; &amp; bar&nbsp;&nbsp;&#8364;5 &lt;not a tag&gt;</p>", 4000)
            .ShouldBe("Café & bar €5 <not a tag>");
    }

    [Theory]
    [InlineData("<div>Open <b>never closed", "Open never closed")]
    [InlineData("Text <a href=\"x>y\">link</a> after", "Text link after")]
    [InlineData("a < b and c > d", "a < b and c > d")]
    [InlineData("Before <script>alert(1)", "Before alert(1)")]
    [InlineData("<html><head><title>Hidden</title><body><p>Kept</p></body>", "Kept")]
    [InlineData("<head><meta charset=\"utf-8\">Kept after an unclosed head", "Kept after an unclosed head")]
    [InlineData("<span class=x title=it's>Don't stop</span> here", "Don't stop here")]
    [InlineData("<p data-x = 'a>b'>Quoted</p>", "Quoted")]
    [InlineData("Before <p class=\"unterminated", "Before class=\"unterminated")]
    [InlineData("<<<>>></></p><!--", "<<<>>>")]
    [InlineData("", "")]
    public void Malformed_html_never_throws(string html, string expected)
    {
        BodyCleaner.Clean(null, html, 4000).ShouldBe(expected);
    }

    [Fact]
    public void Long_text_is_truncated_with_a_marker()
    {
        var cleaned = BodyCleaner.Clean(new string('a', 50) + " " + new string('b', 50), null, 60);

        cleaned.ShouldBe(new string('a', 50) + " " + new string('b', 9) + " " + BodyCleaner.TruncatedMarker);
    }

    [Fact]
    public void Long_html_is_truncated_after_entities_and_tags()
    {
        var html = string.Concat(Enumerable.Repeat("<p>x&amp;y</p>", 10_000));

        var cleaned = BodyCleaner.Clean(null, html, 10);

        cleaned.ShouldBe("x&y\nx&y\nx& " + BodyCleaner.TruncatedMarker);
    }

    [Fact]
    public void Truncation_does_not_split_a_surrogate_pair()
    {
        var cleaned = BodyCleaner.Clean("abc\U0001F600def", null, 4);

        cleaned.ShouldBe("abc " + BodyCleaner.TruncatedMarker);
    }

    [Fact]
    public void Text_within_the_limit_is_not_marked()
    {
        BodyCleaner.Clean("short", null, 5).ShouldBe("short");
    }

    [Fact]
    public void No_body_gives_empty_text()
    {
        BodyCleaner.Clean(null, null, 4000).ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("<p>Kept</p><a href=\"x>Body text</a>", "Kept\nBody text")]
    [InlineData("<p>Kept</p><!-- never closed <p>Body text</p>", "Kept\nnever closed\nBody text")]
    [InlineData("<p>Kept</p><b Body text", "Kept\nBody text")]
    [InlineData("<p>Kept</p><script>x</script Body text", "Kept\nBody text")]
    public void Unterminated_markup_keeps_the_rest_of_the_body(string html, string expected)
    {
        BodyCleaner.Clean(null, html, 4000).ShouldBe(expected);
    }
}
