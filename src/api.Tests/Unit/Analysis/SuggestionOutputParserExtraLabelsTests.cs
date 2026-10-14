using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Prompts;

namespace GmailOrganiser.Tests.Unit.Analysis;

public sealed class SuggestionOutputParserExtraLabelsTests
{
    private const string Parent = "Types";
    private static readonly HashSet<string> Ids = ["m1"];
    private static readonly string[] Configured = ["Action/Test", "Delete/Test"];

    private static SuggestionParseContext Context(params string[] allowed) => new(LabelTreeIndex.Empty, Configured, AllowedExtraLabels: ExtraLabelsOutput.Allowed(allowed));

    private static string Item(string extras, string label = "Topic/Sub") =>
        $$"""{"suggestions":[{"id":"m1","topicLabel":"{{label}}","mailType":"receipt","needsAction":false,"toBeDeleted":false,"unsubscribeSuggested":false,"confidence":0.9,"reason":"Synthetic reason","documentTypeLabel":"Types/Invoice"{{extras}}}]}""";

    [Theory]
    [InlineData("")]
    [InlineData(",\"extraLabels\":null")]
    [InlineData(",\"extraLabels\":\"Extra/A\"")]
    [InlineData(",\"extraLabels\":{}")]
    [InlineData(",\"extraLabels\":[]")]
    [InlineData(",\"extraLabels\":[42,null,true,[\"Extra/A\"]]")]
    public void Missing_or_unusable_is_empty_without_a_note(string extras)
    {
        var result = SuggestionOutputParser.Parse(Item(extras), Ids, documentTypeParent: Parent, context: Context("Extra/A"));

        result.Errors.ShouldBeEmpty();
        result.Dropped.ShouldBeEmpty();
        result.Valid.ShouldHaveSingleItem().ExtraLabels.ShouldBeEmpty();
    }

    [Fact]
    public void Allowed_entries_are_kept_in_the_allowed_spelling_and_the_rest_dropped_with_a_note()
    {
        var json = Item(""","extraLabels":[" extra/b ","Extra/Invented","EXTRA/A","Extr/A","Extra/A"]""");

        var result = SuggestionOutputParser.Parse(json, Ids, documentTypeParent: Parent, context: Context("Extra/A", "Extra/B", "Extra/C"));

        result.Errors.ShouldBeEmpty();
        result.Valid.ShouldHaveSingleItem().ExtraLabels.ShouldBe(["Extra/A", "Extra/B"]);
        result.Dropped.ShouldHaveSingleItem().ShouldBe("Email 'm1': 2 'extraLabels' entries neither a similar past decision's extra nor an existing label the comment names; dropped.");
    }

    [Fact]
    public void Blank_entries_are_ignored_and_quoted_entries_match_as_shown()
    {
        var json = Item(""","extraLabels":[""," ","``","`extra/b`","Extra, C"]""");

        var result = SuggestionOutputParser.Parse(json, Ids, documentTypeParent: Parent, context: Context(" Extra/B ", "Extra, C"));

        result.Dropped.ShouldBeEmpty();
        result.Valid.ShouldHaveSingleItem().ExtraLabels.ShouldBe(["Extra, C", "Extra/B"]);
    }

    [Fact]
    public void Without_allowed_extras_invented_ones_never_reach_a_suggestion()
    {
        var json = Item(""","extraLabels":["Extra/A","Extra/B"]""");

        var result = SuggestionOutputParser.Parse(json, Ids, documentTypeParent: Parent, context: Context());
        var defaulted = SuggestionOutputParser.Parse(json, Ids, documentTypeParent: Parent);

        result.Errors.ShouldBeEmpty();
        result.Valid.ShouldHaveSingleItem().ExtraLabels.ShouldBeEmpty();
        result.Dropped.ShouldHaveSingleItem().ShouldContain("'extraLabels'");
        defaulted.Valid.ShouldHaveSingleItem().ExtraLabels.ShouldBeEmpty();
    }

    [Fact]
    public void Clean_removes_topic_document_type_and_configured_labels_and_caps_the_list()
    {
        var allowed = new[] { "Topic/Sub", "Types/Invoice", "action/test", "Extra/A", "Extra/B", "Extra/C", "Extra/D", "Extra/E", "Extra/F" };
        var json = Item($",\"extraLabels\":{JsonSerializer.Serialize(allowed)}");

        var result = SuggestionOutputParser.Parse(json, Ids, documentTypeParent: Parent, context: Context([.. allowed, "Action/Test"]));

        result.Dropped.ShouldBeEmpty();
        result.Valid.ShouldHaveSingleItem().ExtraLabels.ShouldBe(["Extra/A", "Extra/B", "Extra/C", "Extra/D", "Extra/E"]);
    }

    [Fact]
    public void Reader_works_on_one_item()
    {
        using var doc = JsonDocument.Parse("""{"extraLabels":["extra/a","Other"]}""");
        var dropped = new List<string>();

        var kept = ExtraLabelsOutput.Read("m1", doc.RootElement, "Topic/Sub", null, Context("Extra/A"), dropped);

        kept.ShouldBe(["Extra/A"]);
        dropped.ShouldHaveSingleItem().ShouldStartWith("Email 'm1': 1 'extraLabels' entry");
    }
}
