using GmailOrganiser.Analysis;

namespace GmailOrganiser.Tests.Unit.Analysis;

/// <summary>The document-type depth left under a parent, the capped list of existing types and the label tree's spelling (#336).</summary>
public sealed class DocumentTypePathTests
{
    [Theory]
    [InlineData("Types", 3, "1 to 3 levels")]
    [InlineData(" A/B ", 3, "1 to 3 levels")]
    [InlineData("A/B/C", 2, "1 to 2 levels")]
    [InlineData("A/B/C/D", 1, "1 level")]
    public void Depth_under_a_parent_stays_within_gmails_five_levels(string parent, int depth, string words)
    {
        DocumentTypePath.MaxDepthUnder(parent).ShouldBe(depth);
        DocumentTypePath.LevelsUnder(parent).ShouldBe(words);
    }

    [Fact]
    public void Children_fill_the_cap_shallowest_level_first_and_say_when_more_exist()
    {
        var deep = Enumerable.Range(0, DocumentTypePath.MaxChildren).Select(i => $"Types/Bills/Kind/B{i:000}");
        var labels = deep.Append("Types/Tax").Append("Types/Bills").Append("Types/Bills/Kind").ToList();

        var children = DocumentTypePath.Children("Types", labels, out var truncated);

        truncated.ShouldBeTrue();
        children.Count.ShouldBe(DocumentTypePath.MaxChildren);
        children.ShouldContain("Types/Tax");
        children.Take(3).ShouldBe(["Types/Bills", "Types/Bills/Kind", "Types/Bills/Kind/B000"]);
        children.ShouldBe(children.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Children_skip_blank_and_too_deep_segments()
    {
        DocumentTypePath.Children("Types", ["Types/A", "Types/ /B", "Types/A/B/C/D", "Types/A/B/C"], out var truncated)
            .ShouldBe(["Types/A", "Types/A/B/C"]);
        truncated.ShouldBeFalse();
    }

    [Theory]
    [InlineData("types/utilities/Water", "Types/Utilities/Water")]
    [InlineData("TYPES/UTILITIES/ELECTRICITY", "Types/Utilities/Electricity")]
    [InlineData("types/Tax/Annual", "Types/Tax/Annual")]
    [InlineData("other/Kind", "other/Kind")]
    public void Respell_takes_the_longest_existing_prefix_in_the_trees_spelling(string label, string expected)
    {
        var index = new LabelTreeIndex(["Types", "Types/Utilities", "Types/Utilities/Electricity"]);

        index.Respell(label).ShouldBe(expected);
    }
}
