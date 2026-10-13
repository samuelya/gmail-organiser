using GmailOrganiser.Gmail;
using GmailOrganiser.Rules;
using GmailOrganiser.Rules.Review;

namespace GmailOrganiser.Tests.Unit.Rules;

// #557: a split group (one Gmail filter per user label, same criteria) is judged as the one filter the user sees.
public sealed partial class FilterChecksTests
{
    [Fact]
    public void A_split_group_with_no_recent_matches_is_one_finding_that_deletes_the_whole_group()
    {
        FilterRow[] group =
        [
            Row("g1", new(Subject: "old"), new(["Label_1"], ["INBOX"])),
            Row("g2", new(Subject: "old"), new(["Label_2"], ["INBOX"])),
        ];

        var finding = FilterChecks.Run(group, Labels, _ => 0, 365).ShouldHaveSingleItem();

        finding.Kind.ShouldBe(FilterFindingKind.NoRecentMatches);
        finding.FilterIds.ShouldBe(["g1", "g2"]);
        finding.Fix.ShouldBe(new FilterFix(FilterFixKind.Delete, ["g1", "g2"]), new FixComparer());
    }

    [Fact]
    public void Split_groups_with_the_same_labels_merge_once_into_one_group()
    {
        FilterRow[] rows =
        [
            Row("a1", new(From: "a@example.com"), new(["Label_1"], [])),
            Row("a2", new(From: "a@example.com"), new(["Label_2"], [])),
            Row("b1", new(From: "b@example.com"), new(["Label_1"], []), seenDaysLater: 1),
            Row("b2", new(From: "b@example.com"), new(["Label_2"], []), seenDaysLater: 1),
        ];

        var finding = Run(rows).ShouldHaveSingleItem();

        finding.Kind.ShouldBe(FilterFindingKind.Mergeable);
        finding.Fix.DeleteFilterIds.ShouldBe(["a1", "a2", "b1", "b2"]);
        var create = finding.Fix.Create.ShouldNotBeNull();
        create.Criteria.From.ShouldBe("a@example.com OR b@example.com");
        create.Action.AddLabelIds.ShouldBe(["Label_1", "Label_2"]);
    }

    [Fact]
    public void A_deleted_label_in_a_split_group_deletes_only_its_member()
    {
        FilterRow[] group =
        [
            Row("g1", new(From: "shop@example.com"), new(["Label_1"], ["INBOX"])),
            Row("g2", new(From: "shop@example.com"), new(["Label_gone"], ["INBOX"])),
        ];

        var finding = Run(group).ShouldHaveSingleItem();

        finding.Kind.ShouldBe(FilterFindingKind.DeletedLabel);
        finding.FilterIds.ShouldBe(["g1", "g2"]);
        finding.Fix.ShouldBe(new FilterFix(FilterFixKind.DropLabel, ["g2"]), new FixComparer());
    }

    [Fact]
    public void A_group_member_left_with_no_user_label_is_deleted_not_re_created()
    {
        var group = Guid.NewGuid();
        var kept = Row("g1", new(From: "shop@example.com"), new(["Label_1"], ["INBOX"]));
        var gone = Row("g2", new(From: "shop@example.com"), new(["Label_gone"], ["INBOX", "UNREAD"]));
        kept.GroupId = gone.GroupId = group;

        var finding = Run([kept, gone]).ShouldHaveSingleItem();

        finding.Kind.ShouldBe(FilterFindingKind.DeletedLabel);
        finding.Fix.ShouldBe(new FilterFix(FilterFixKind.Delete, ["g2"]), new FixComparer());
    }
}
