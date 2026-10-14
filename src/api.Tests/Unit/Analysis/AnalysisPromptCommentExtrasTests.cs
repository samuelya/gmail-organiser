using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Memory;

namespace GmailOrganiser.Tests.Unit.Analysis;

/// <summary>A re-analysis comment may ask for an existing label as an extra (#582).</summary>
public sealed class AnalysisPromptCommentExtrasTests
{
    private static readonly string[] Tree = ["Topic", "Topic/Sub", "Archive/Kept", "Action/Test", "Short"];
    private static readonly string[] Configured = ["Action/Test", "Delete/Test"];
    private static readonly HashSet<string> Ids = ["id1"];
    private static readonly AnalysisPromptBuilder Builder = new(PromptTemplate.BuiltIn);

    private static readonly EmailForPrompt Email = new(
        "id1", "sender1@example.com", "Sender 1", "Subject 1", new DateTimeOffset(2026, 3, 4, 9, 30, 0, TimeSpan.Zero),
        "updates", false, false, "Synthetic body", []);

    private static readonly MemoryHint HintWithExtra =
        new("sender1@example.com", null, "Topic", false, false, "approved", 0.5) { ExtraLabels = ["Extra/Hint"] };

    private static PromptInput Input(string? comment, params MemoryHint[] memory) =>
        new([Email], Tree, memory, null, "Action/Test", "Delete/Test", OwnerGuidance: comment);

    private static string Answer(params string[] extras) =>
        $$"""{"suggestions":[{"id":"id1","topicLabel":"Topic","mailType":"notification","needsAction":false,"toBeDeleted":false,"unsubscribeSuggested":false,"confidence":0.9,"reason":"Synthetic reason","extraLabels":[{{string.Join(',', extras.Select(e => $"\"{e}\""))}}]}]}""";

    private static ParsedSuggestions Parse(PromptInput input, params string[] extras) =>
        SuggestionOutputParser.Parse(Answer(extras), Ids, context: new SuggestionParseContext(
            new LabelTreeIndex(Tree), Configured, AllowedExtraLabels: Builder.AllowedExtraLabels(input)));

    [Fact]
    public void Comment_naming_an_existing_label_keeps_it_in_the_tree_spelling()
    {
        var result = Parse(Input("These also need the archive/kept label."), "ARCHIVE/KEPT");

        result.Dropped.ShouldBeEmpty();
        result.Valid.ShouldHaveSingleItem().ExtraLabels.ShouldBe(["Archive/Kept"]);
    }

    [Fact]
    public void Label_named_but_not_in_the_tree_is_dropped_with_a_note()
    {
        var result = Parse(Input("Add Archive/Invented as well."), "Archive/Invented");

        result.Valid.ShouldHaveSingleItem().ExtraLabels.ShouldBeEmpty();
        result.Dropped.ShouldHaveSingleItem().ShouldContain("'extraLabels'");
    }

    [Fact]
    public void Without_a_comment_only_the_hints_extras_are_allowed()
    {
        Builder.AllowedExtraLabels(Input(null, HintWithExtra)).Values.ShouldBe(["Extra/Hint"]);
        Builder.AllowedExtraLabels(Input(" \n ")).ShouldBeEmpty();

        var result = Parse(Input(null, HintWithExtra), "Extra/Hint", "Archive/Kept");

        result.Valid.ShouldHaveSingleItem().ExtraLabels.ShouldBe(["Extra/Hint"]);
        result.Dropped.ShouldHaveSingleItem();
    }

    [Fact]
    public void Comment_adds_to_the_hints_extras_and_configured_or_topic_labels_are_still_cleaned()
    {
        var input = Input("Use Archive/Kept, Action/Test and Topic too.", HintWithExtra);

        Builder.AllowedExtraLabels(input).Values.ShouldBe(["Extra/Hint", "Topic", "Archive/Kept", "Action/Test"], ignoreOrder: true);
        Parse(input, "Extra/Hint", "Archive/Kept", "Action/Test", "Topic").Valid.ShouldHaveSingleItem()
            .ExtraLabels.ShouldBe(["Archive/Kept", "Extra/Hint"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("Use Topic/Sub here.", new[] { "Topic/Sub" })]
    [InlineData("Shortly, nothing.", new string[0])]
    [InlineData("short", new[] { "Short" })]
    [InlineData("(Short) and topic.", new[] { "Topic", "Short" })]
    [InlineData("Sub/Topic/Subway", new string[0])]
    [InlineData("pre-short, short_old, short.v2 and Short-2023", new string[0])]
    [InlineData("Not short. Topic - and short_.", new[] { "Short", "Topic" })]
    public void Only_whole_names_count(string comment, string[] named)
    {
        ExtraLabelsOutput.NamedIn(comment, Tree).ShouldBe(named, ignoreOrder: true);
    }

    [Fact]
    public void Comment_without_hint_extras_gets_the_guided_instruction_and_not_the_hint_one()
    {
        var user = Builder.Build(Input("Synthetic comment"))[1].Text;
        user.ShouldContain(AnalysisPromptBuilder.GuidedExtraLabelsInstruction);
        user.ShouldNotContain(AnalysisPromptBuilder.ExtraLabelsInstruction);

        var withHint = Builder.Build(Input("Synthetic comment", HintWithExtra))[1].Text;
        withHint.ShouldContain(AnalysisPromptBuilder.GuidedExtraLabelsInstruction);
        withHint.ShouldNotContain(AnalysisPromptBuilder.ExtraLabelsInstruction);
    }

    [Fact]
    public void Without_a_comment_the_prompt_has_no_guided_instruction()
    {
        var user = Builder.Build(Input(null))[1].Text;
        user.ShouldNotContain(AnalysisPromptBuilder.GuidedExtraLabelsInstruction);
        user.ShouldNotContain(AnalysisPromptBuilder.ExtraLabelsInstruction);

        var withHint = Builder.Build(Input(null, HintWithExtra))[1].Text;
        withHint.ShouldContain(AnalysisPromptBuilder.ExtraLabelsInstruction);
        withHint.ShouldNotContain(AnalysisPromptBuilder.GuidedExtraLabelsInstruction);
    }

    [Fact]
    public void Custom_template_without_the_comment_placeholder_still_appends_the_guidance()
    {
        var builder = new AnalysisPromptBuilder(PromptTemplate.FromSettings("Classify.\n{{memory}}\nEmails: {{emails}}"));

        builder.Build(Input("Use Archive/Kept."))[1].Text.ShouldContain(AnalysisPromptBuilder.GuidedExtraLabelsInstruction);
        builder.AllowedExtraLabels(Input("Use Archive/Kept.")).Values.ShouldBe(["Archive/Kept"]);
    }

    [Fact]
    public void Only_labels_the_prompt_shows_can_be_named()
    {
        var tree = Enumerable.Range(0, AnalysisPromptBuilder.MaxLabelTreeEntries + 1).Select(i => $"Synthetic {i:000}").ToList();

        AnalysisPromptBuilder.CommentNamedLabels("Use Synthetic 499 and Synthetic 500.", tree).ShouldBe(["Synthetic 499"]);
        AnalysisPromptBuilder.CommentNamedLabels(" ", tree).ShouldBeEmpty();
    }
}
