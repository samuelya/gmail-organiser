using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit.Analysis;

public sealed class ApprovedLabelSetTests
{
    private static SuggestionOutput Output(string id, string label, bool isNew, double confidence = 0.9, string? type = null) =>
        new(id, label, isNew, false, false, false, confidence, "Synthetic reason", type);

    private static ParsedSuggestions Parsed(params SuggestionOutput[] valid) => new(valid, [], null);

    [Fact]
    public void Locked_moves_a_new_topic_label_to_the_proposal_with_a_note_and_keeps_existing_ones()
    {
        var set = new ApprovedLabelSet(true, [], 3);
        var proposed = Output("p", "Topic/Proposed", true) with { ProposedNewLabel = "Topic/Proposed" };

        var result = set.Apply(Parsed(Output("n", "Topic/New", true), Output("e", "Topic", false), proposed));

        result.Valid.Select(o => (o.Id, o.ProposedNewLabel, o.IsNewLabel)).ShouldBe(
            [("n", "Topic/New", true), ("e", null, false), ("p", "Topic/Proposed", true)]);
        result.Dropped.ShouldHaveSingleItem().ShouldContain("'n'");
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Unlocked_without_blocked_names_leaves_the_answer_as_parsed()
    {
        var parsed = Parsed(Output("n", "Topic/New", true));

        new ApprovedLabelSet(false, [], 3).Apply(parsed).ShouldBeSameAs(parsed);
    }

    [Theory]
    [InlineData("Synthetic Blocked")]
    [InlineData("Topic/synthetic blocked")]
    [InlineData("Topic/SYNTHETIC BLOCKED/Leaf")]
    public void A_blocked_name_at_any_level_fails_the_email(string label)
    {
        var set = new ApprovedLabelSet(false, [" Synthetic Blocked "], 3);

        var result = set.Apply(Parsed(Output("b", label, false), Output("ok", "Topic/Synthetic Blocked Not", false)));

        result.Valid.ShouldHaveSingleItem().Id.ShouldBe("ok");
        result.Errors.ShouldHaveSingleItem().ShouldBe($"Email 'b': the label {ApprovedLabelSet.BlockedError}.");
    }

    [Fact]
    public void A_blocked_document_type_fails_the_email_too()
    {
        var set = new ApprovedLabelSet(true, ["Synthetic Blocked"], 3);

        var result = set.Apply(Parsed(Output("d", "Topic", false, type: "Types/Synthetic Blocked")));

        result.Valid.ShouldBeEmpty();
        result.Errors.ShouldHaveSingleItem().ShouldStartWith("Email 'd'");
    }

    [Fact]
    public void Past_the_cap_a_new_label_keeps_its_label_at_capped_confidence()
    {
        var set = new ApprovedLabelSet(true, [], 2, admitted: ["Topic/Seeded"]);

        set.Admit(Output("1", "topic/seeded", true)).Confidence.ShouldBe(0.9);
        set.Admit(Output("2", "Topic/First", true)).Confidence.ShouldBe(0.9);
        var capped = set.Admit(Output("3", "Topic/Second", true));
        set.Admit(Output("4", "Topic/First", true)).Confidence.ShouldBe(0.9);
        var low = set.Admit(Output("5", "Topic/Second", true, confidence: 0.3));

        (capped.TopicLabel, capped.Confidence, capped.Reason).ShouldBe(
            ("Topic/Second", ApprovedLabelSet.CappedConfidence, "Synthetic reason" + ApprovedLabelSet.CapNote));
        low.Confidence.ShouldBe(0.3);
        set.Admit(capped).Reason.ShouldBe(capped.Reason);
        set.Admit(Output("6", "Topic", false)).Confidence.ShouldBe(0.9);
    }

    [Fact]
    public void Unlocked_runs_have_no_cap_and_a_zero_cap_caps_every_new_label()
    {
        new ApprovedLabelSet(false, [], 0).Admit(Output("1", "Topic/New", true)).Confidence.ShouldBe(0.9);
        new ApprovedLabelSet(true, [], 0).Admit(Output("1", "Topic/New", true)).Confidence.ShouldBe(ApprovedLabelSet.CappedConfidence);
    }

    [Fact]
    public void The_prompt_lists_blocked_names_and_the_lock_only_when_set()
    {
        var builder = new AnalysisPromptBuilder(PromptTemplate.BuiltIn);
        var input = new PromptInput([], ["Topic"], [], null, "Action", "Delete");

        var plain = builder.Build(input)[0].Text;
        var set = builder.Build(input with { BlockedLabels = ["Synthetic One", "Synthetic\nTwo"], TaxonomyLocked = true })[0].Text;

        plain.ShouldNotContain(AnalysisPromptBuilder.BlockedLabelsHeading);
        plain.ShouldNotContain(AnalysisPromptBuilder.TaxonomyLockedText);
        set.ShouldContain($"{AnalysisPromptBuilder.BlockedLabelsHeading} `Synthetic One`, `Synthetic Two`.");
        set.ShouldContain(AnalysisPromptBuilder.TaxonomyLockedText);
        set.ShouldNotContain("{{blockedLabels}}");
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(51, null)]
    [InlineData(null, "Synthetic/Path")]
    [InlineData(null, " ")]
    [InlineData(null, "Synthetic\tName")]
    public void Settings_reject_an_out_of_range_cap_and_unusable_blocked_names(int? cap, string? name)
    {
        var errors = SettingsValidation.Validate(new UpdateSettingsRequest(
            null, null, null, null, AnalysisMaxNewLabelsPerRun: cap, AnalysisBlockedLabels: name is null ? null : [name]));

        errors.Keys.ShouldHaveSingleItem().ShouldBe(cap is null ? "analysisBlockedLabels" : "analysisMaxNewLabelsPerRun");
    }

    [Fact]
    public void Settings_accept_the_limits_and_reject_one_name_too_many()
    {
        string[] names = [.. Enumerable.Range(0, SettingsValidation.MaxBlockedLabels).Select(i => $"Synthetic {i}")];

        SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, TaxonomyLocked: true,
            AnalysisMaxNewLabelsPerRun: 0, AnalysisBlockedLabels: [.. names[..^1], new string('x', SettingsValidation.MaxBlockedLabelLength)]))
            .ShouldBeEmpty();
        SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, AnalysisBlockedLabels: [.. names, "Synthetic extra"]))
            .Keys.ShouldBe(["analysisBlockedLabels"]);
    }
}
