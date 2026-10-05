using GmailOrganiser.Gmail;
using GmailOrganiser.Rules.Labels;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit.Rules;

public sealed class LabelPlanBuilderTests
{
    private static readonly Dictionary<string, IReadOnlyList<string>> NoFilters = [];

    [Theory]
    [InlineData("Project_Receipts", "project receipt")]
    [InlineData("  Project -- Receipts ", "project receipt")]
    [InlineData("Travel.Plans/Hotel-Bookings", "travel plans/hotel booking")]
    [InlineData("News", "new")]
    [InlineData("s", "s")]
    public void Normalise_lower_cases_collapses_separators_and_drops_the_last_trailing_s(string name, string expected) =>
        LabelPlanBuilder.Normalise(name).ShouldBe(expected);

    [Theory]
    [InlineData("Receipts", "receipt", true)]
    [InlineData("Project-Notes", "Project Notes", true)]
    [InlineData("Newsletter", "Newsleter", true)]
    [InlineData("Newsletters", "Newsletter", true)]
    [InlineData("Shoping", "Shopping", true)]
    [InlineData("Bils", "Bills", false)]
    [InlineData("Invoices 2023", "Invoices 2024", false)]
    [InlineData("Sprint 1", "Sprint 10", false)]
    [InlineData("Room 10", "Room 100", false)]
    [InlineData("Invoice", "Invoise", true)]
    [InlineData("Family", "Friends", false)]
    [InlineData("Health", "Wealth", false)]
    [InlineData("Team A", "Team B", false)]
    [InlineData("Unit 1A", "Unit 1B", false)]
    [InlineData("Clients/Jan", "Clients/Dan", false)]
    [InlineData("Clients/Example", "Clients/Exmple", true)]
    [InlineData("Alpha/Newsletter", "Alphb/Newsletter", false)]
    public void Near_duplicates_are_equal_normalised_or_one_edit_apart_from_six_chars(string a, string b, bool expected)
    {
        LabelPlanBuilder.AreNearDuplicates(a, b).ShouldBe(expected);
        LabelPlanBuilder.AreNearDuplicates(b, a).ShouldBe(expected);
    }

    [Fact]
    public void An_empty_label_is_proposed_for_deletion_with_its_filters()
    {
        var items = Build(
            [User("Label_1", "Example", 5), User("Label_2", "Synthetic Empty", 0)],
            filters: new() { ["Label_2"] = ["filter-1"] });

        var item = items.ShouldHaveSingleItem();
        item.Kind.ShouldBe(LabelPlanItemKind.Empty);
        item.LabelId.ShouldBe("Label_2");
        item.MessageCount.ShouldBe(0);
        item.AffectedFilterIds.ShouldBe(["filter-1"]);
        item.Status.ShouldBe(LabelPlanItemStatus.Proposed);
        item.Rationale.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_empty_parent_is_kept_for_its_children()
    {
        var items = Build([User("Label_1", "Example", 0), User("Label_2", "Example/Nested", 3)]);

        items.ShouldBeEmpty();
    }

    [Fact]
    public void A_near_duplicate_merges_into_the_label_with_more_messages()
    {
        var items = Build([User("Label_1", "Receipt", 2), User("Label_2", "Receipts", 9), User("Label_3", "receipts-", 1)]);

        items.Count.ShouldBe(2);
        items.ShouldAllBe(i => i.Kind == LabelPlanItemKind.NearDuplicate && i.TargetLabelId == "Label_2" && i.TargetLabelName == "Receipts");
        items.Select(i => i.LabelId).ShouldBe(["Label_1", "Label_3"], ignoreOrder: true);
    }

    [Fact]
    public void A_count_tie_merges_into_the_older_label()
    {
        var items = Build([User("Label_7", "Newsletter", 4), User("Label_2", "Newsletters", 4)]);

        var item = items.ShouldHaveSingleItem();
        item.LabelId.ShouldBe("Label_2");
        item.TargetLabelId.ShouldBe("Label_7");
    }

    [Fact]
    public void A_flat_label_nests_under_an_existing_top_level_prefix()
    {
        var items = Build([User("Label_1", "Travel", 3), User("Label_2", "travel-Hotels", 2), User("Label_3", "Travel/Flights", 1)]);

        var item = items.ShouldHaveSingleItem();
        item.Kind.ShouldBe(LabelPlanItemKind.Nest);
        item.LabelId.ShouldBe("Label_2");
        item.ProposedName.ShouldBe("Travel/Hotels");
    }

    [Fact]
    public void The_longest_existing_prefix_wins_and_a_parent_is_not_renamed()
    {
        var items = Build([User("Label_1", "Home", 3), User("Label_2", "Home Office", 3), User("Label_3", "Home Office.Supplies", 2)]);

        var item = items.ShouldHaveSingleItem();
        item.LabelId.ShouldBe("Label_3");
        item.ProposedName.ShouldBe("Home Office/Supplies");
    }

    [Fact]
    public void Flat_labels_sharing_a_prefix_nest_under_it()
    {
        var items = Build([User("Label_1", "Work_Alpha", 3), User("Label_2", "work-Beta", 2), User("Label_3", "Hobby-Chess", 1)]);

        items.Count.ShouldBe(2);
        items.Single(i => i.LabelId == "Label_1").ProposedName.ShouldBe("Work/Alpha");
        items.Single(i => i.LabelId == "Label_2").ProposedName.ShouldBe("Work/Beta");
    }

    [Fact]
    public void Nested_labels_and_system_labels_get_no_item()
    {
        var items = Build(
        [
            User("Label_1", "Example", 3),
            User("Label_2", "Example/Sub-Topic", 0),
            (new GmailLabel("CATEGORY_UPDATES", "CATEGORY_UPDATES", GmailLabelType.System), 0),
        ]);

        items.ShouldHaveSingleItem().Kind.ShouldBe(LabelPlanItemKind.Empty);
        items.ShouldNotContain(i => i.Kind == LabelPlanItemKind.Nest);
        items.ShouldNotContain(i => i.LabelId == "CATEGORY_UPDATES");
    }

    [Fact]
    public void A_flat_label_duplicating_a_nested_one_merges_instead_of_nesting()
    {
        var items = Build([User("Label_1", "Travel", 3), User("Label_2", "Travel-Hotels", 2), User("Label_3", "Travel/Hotels", 5)]);

        var item = items.ShouldHaveSingleItem();
        item.Kind.ShouldBe(LabelPlanItemKind.NearDuplicate);
        item.LabelId.ShouldBe("Label_2");
        item.TargetLabelId.ShouldBe("Label_3");
    }

    [Fact]
    public void Protected_labels_are_never_proposed_nor_merged_into()
    {
        var items = Build(
        [
            User("Label_1", "Synthetic-Action", 0),
            User("Label_2", "Synthetic-Delete", 0),
            User("Label_3", "Synthetic Actions", 1),
            User("Label_4", "Unused", 0),
        ],
        protectedNames: ["synthetic-action", "Synthetic-Delete"]);

        var item = items.ShouldHaveSingleItem();
        item.LabelId.ShouldBe("Label_4");
    }

    [Fact]
    public void Each_label_gets_at_most_one_item()
    {
        var items = Build([User("Label_1", "Travel", 3), User("Label_2", "Travel-Plans", 0), User("Label_3", "Travel-Plan", 4)]);

        items.Select(i => i.LabelId).ShouldBeUnique();
        items.Single(i => i.LabelId == "Label_2").Kind.ShouldBe(LabelPlanItemKind.Empty);
        items.Single(i => i.LabelId == "Label_3").Kind.ShouldBe(LabelPlanItemKind.Nest);
    }

    [Fact]
    public void A_parent_is_never_merged_away()
    {
        var items = Build([User("Label_1", "Receipts", 2), User("Label_2", "Receipts/2023", 1), User("Label_3", "Receipt", 5)]);

        items.ShouldBeEmpty();
    }

    [Fact]
    public void A_label_that_a_nest_moves_under_is_not_merged_away()
    {
        var items = Build([User("Label_1", "Invoice", 2), User("Label_2", "Invoices", 5), User("Label_3", "Invoice-2023", 1)]);

        var item = items.ShouldHaveSingleItem();
        item.Kind.ShouldBe(LabelPlanItemKind.Nest);
        item.LabelId.ShouldBe("Label_3");
        item.ProposedName.ShouldBe("Invoice/2023");
    }

    [Fact]
    public void An_empty_label_is_deleted_when_its_only_would_be_child_is_deleted_too()
    {
        var items = Build([User("Label_1", "Workshop", 0), User("Label_2", "Workshop-Old", 0)]);

        items.Count.ShouldBe(2);
        items.ShouldAllBe(i => i.Kind == LabelPlanItemKind.Empty);
    }

    [Fact]
    public void An_empty_label_stays_while_a_kept_label_nests_under_it()
    {
        var items = Build([User("Label_1", "Workshop", 0), User("Label_2", "Workshop-Old", 3)]);

        var item = items.ShouldHaveSingleItem();
        item.Kind.ShouldBe(LabelPlanItemKind.Nest);
        item.ProposedName.ShouldBe("Workshop/Old");
    }

    [Fact]
    public void Counts_from_fetched_mail_propose_no_empty_label()
    {
        var items = LabelPlanBuilder.Build([User("Label_1", "Example", 5), User("Label_2", "Synthetic Empty", 0)], [], NoFilters, countsAreExact: false);

        items.ShouldBeEmpty();
    }

    [Fact]
    public void Short_distinct_leaves_are_not_merged()
    {
        var items = Build([User("Label_1", "Clients", 1), User("Label_2", "Clients/Dan", 5), User("Label_3", "Clients/Jan", 3)]);

        items.ShouldBeEmpty();
    }

    [Fact]
    public void A_shared_prefix_left_with_one_label_is_not_nested()
    {
        var items = Build([User("Label_1", "Proj-A", 0), User("Label_2", "Proj-B", 4)]);

        var item = items.ShouldHaveSingleItem();
        item.Kind.ShouldBe(LabelPlanItemKind.Empty);
        item.LabelId.ShouldBe("Label_1");
    }

    [Fact]
    public void A_shared_prefix_rationale_counts_the_labels_that_move()
    {
        var items = Build([User("Label_1", "Proj-Alpha", 0), User("Label_2", "Proj-Beta", 4), User("Label_3", "Proj-Gamma", 2)]);

        var nests = items.Where(i => i.Kind == LabelPlanItemKind.Nest).ToList();
        nests.Select(i => i.LabelId).ShouldBe(["Label_2", "Label_3"], ignoreOrder: true);
        nests.ShouldAllBe(i => i.Rationale.StartsWith("2 labels share"));
    }

    [Fact]
    public void Nothing_nests_under_a_protected_label()
    {
        var items = Build(
            [User("Label_1", "Synthetic", 3), User("Label_2", "Synthetic-One", 2), User("Label_3", "Synthetic-Two", 2)],
            protectedNames: ["synthetic"]);

        items.ShouldBeEmpty();
    }

    [Fact]
    public void Protected_names_include_the_apps_script_labels()
    {
        var app = new AppSettings
        {
            AppsScript = new AppsScriptSettings
            {
                Rules = [new ArchiveRule("Synthetic/Rule", 7)],
                RetentionRules = [new RetentionRule("Synthetic/Retain", 365)],
                KeepInInboxLabels = ["Synthetic Keep"],
            },
        };

        LabelPlanService.ProtectedNames(app).ShouldBe([app.ActionLabelName, app.DeleteLabelName, "Synthetic/Rule", "Synthetic/Retain", "Synthetic Keep"]);
    }

    private static (GmailLabel Label, long Count) User(string id, string name, long count) =>
        (new GmailLabel(id, name, GmailLabelType.User), count);

    private static IReadOnlyList<LabelPlanItem> Build(
        IReadOnlyList<(GmailLabel Label, long Count)> labels,
        IReadOnlyCollection<string>? protectedNames = null,
        Dictionary<string, IReadOnlyList<string>>? filters = null) =>
        LabelPlanBuilder.Build(labels, protectedNames ?? [], filters ?? NoFilters);
}
