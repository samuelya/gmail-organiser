using GmailOrganiser.Analysis;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit;

/// <summary>The canonical additional-label list (#522).</summary>
public sealed class ExtraLabelListTests
{
    private static readonly AppSettings Settings = new() { ActionLabelName = "Synthetic/Action", DeleteLabelName = "Synthetic Delete" };
    private static readonly string[] Reserved = ExtraLabelList.Reserved(Settings);

    [Fact]
    public void Clean_trims_drops_invalid_and_duplicates_keeping_the_first_spelling_and_sorts()
    {
        ExtraLabelList.Clean([" beta ", "", "  ", "Alpha", "BETA", "INBOX", "a//b", null], null, null, Reserved)
            .ShouldBe(["Alpha", "beta"]);
    }

    [Fact]
    public void Clean_drops_the_topic_document_type_and_reserved_names_case_insensitively()
    {
        ExtraLabelList.Clean(["Topic", "types/receipt", "synthetic/action", "SYNTHETIC DELETE", "Kept"], " topic ", "Types/Receipt", Reserved)
            .ShouldBe(["Kept"]);
    }

    [Fact]
    public void Clean_keeps_the_first_max_after_sorting()
    {
        ExtraLabelList.Clean(["g", "f", "e", "d", "c", "b", "a"], null, null, []).ShouldBe(["a", "b", "c", "d", "e"]);
        ExtraLabelList.Clean(null, null, null, []).ShouldBeEmpty();
    }

    [Fact]
    public void Normalise_null_is_unchanged_and_empty_clears()
    {
        var errors = new Dictionary<string, string[]>();

        ExtraLabelList.Normalise(null, "Topic", null, Settings, errors).ShouldBe(ExtraLabelsChange.Unchanged);
        var cleared = ExtraLabelList.Normalise([], "Topic", null, Settings, errors);

        (cleared.IsSet, cleared.Labels.Length).ShouldBe((true, 0));
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Normalise_collapses_duplicates_silently()
    {
        var errors = new Dictionary<string, string[]>();

        var change = ExtraLabelList.Normalise(["b", " B ", "a", "c", "d", "e", "E"], "Topic", null, Settings, errors);

        errors.ShouldBeEmpty();
        change.Labels.ShouldBe(["a", "b", "c", "d", "e"]);
    }

    [Fact]
    public void Normalise_names_each_offending_entry_and_changes_nothing()
    {
        var errors = new Dictionary<string, string[]>();

        var change = ExtraLabelList.Normalise(
            ["", "INBOX", "topic", "Types/Receipt", "synthetic delete", "Ok", "a", "b", "c", "d"], "Topic", "types/receipt", Settings, errors);

        change.ShouldBe(ExtraLabelsChange.Unchanged);
        errors[ExtraLabelList.Field].ShouldBe([
            "Entry 1: Must not be blank.",
            "Entry 2 'INBOX': Not a label path Gmail accepts.",
            "Entry 3 'topic': Must differ from the topic label.",
            "Entry 4 'Types/Receipt': Must differ from the document-type label.",
            "Entry 5 'synthetic delete': The app's action and delete labels are set by the outcome.",
            $"At most {ExtraLabelList.Max} additional labels.",
        ]);
    }

    [Fact]
    public void New_lists_the_labels_gmail_lacks_and_none_when_the_list_is_unknown()
    {
        var names = new Dictionary<string, string> { ["Label_1"] = "Known " };

        ExtraLabelList.New(["known", "Fresh"], ExtraLabelList.Existing(names)).ShouldBe(["Fresh"]);
        ExtraLabelList.Existing(null).ShouldBeNull();
        ExtraLabelList.New(["Fresh"], null).ShouldBeEmpty();
    }

    [Fact]
    public void SameList_compares_canonical_lists_ordinally_in_order_as_the_sql_grouping_does()
    {
        ExtraLabelList.SameList(["a", "B"], ["a", "B"]).ShouldBeTrue();
        ExtraLabelList.SameList(["a", "B"], ["A", "b"]).ShouldBeFalse();
        ExtraLabelList.SameList(["a"], ["a", "b"]).ShouldBeFalse();
        ExtraLabelList.SameList([], []).ShouldBeTrue();
    }

    [Fact]
    public void Merge_keeps_the_current_ones_and_reports_the_added_ones_the_cap_leaves_out()
    {
        var (labels, capped) = ExtraLabelList.Merge(
            ["Extra/A", "Extra/B", "Extra/C", "Extra/D"], ["extra/a", "Topic", "Extra/F", "Extra/E"], "Topic", null, []);

        labels.ShouldBe(["Extra/A", "Extra/B", "Extra/C", "Extra/D", "Extra/E"]);
        capped.ShouldBe(["Extra/F"]);
        var (unchanged, none) = ExtraLabelList.Merge(["Extra/A"], [], null, null, []);
        (unchanged.Single(), none.Length).ShouldBe(("Extra/A", 0));
    }

    [Fact]
    public void WithNotAdded_names_the_labels_before_a_trailing_cap_note()
    {
        ExtraLabelList.WithNotAdded("Reason.", []).ShouldBe("Reason.");
        ExtraLabelList.WithNotAdded("Reason.", ["Extra/B", "extra/b", "Extra/A"])
            .ShouldBe($"Reason. {ExtraLabelList.NotAddedNote} Extra/B, Extra/A.");
        ExtraLabelList.WithNotAdded("Reason." + ApprovedLabelSet.CapNote, ["Extra/A"])
            .ShouldBe($"Reason. {ExtraLabelList.NotAddedNote} Extra/A.{ApprovedLabelSet.CapNote}");
    }
}
