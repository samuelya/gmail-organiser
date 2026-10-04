using System.Globalization;
using GmailOrganiser.Gmail;
using GmailOrganiser.Rules;
using GmailOrganiser.Rules.Review;

namespace GmailOrganiser.Tests.Unit.Rules;

public sealed class FilterChecksTests
{
    private static readonly DateTimeOffset Seen = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly GmailLabel[] Labels =
    [
        new("INBOX", "INBOX", GmailLabelType.System),
        new("UNREAD", "UNREAD", GmailLabelType.System),
        new("Label_1", "Synthetic/Shop", GmailLabelType.User),
        new("Label_2", "Synthetic/News", GmailLabelType.User),
    ];

    private static readonly Func<FilterRow, int?> NotEvaluable = _ => null;

    [Fact]
    public void Normalise_trims_and_lower_cases_text_and_treats_absent_booleans_as_false()
    {
        var a = new GmailFilterCriteria(From: " Shop@Example.com ", Subject: "Receipt", HasAttachment: null);
        var b = new GmailFilterCriteria(From: "shop@example.com", Subject: "  receipt", HasAttachment: false, To: " ");

        FilterChecks.Normalise(a).ShouldBe(FilterChecks.Normalise(b));
        FilterChecks.Normalise(a with { Size = 10, SizeComparison = GmailSizeComparison.Larger }).ShouldNotBe(FilterChecks.Normalise(b));
    }

    [Fact]
    public void Duplicate_deletes_the_later_seen_copies_and_ignores_label_order_and_case()
    {
        var first = Row("f1", new(From: "shop@example.com"), new(["Label_1", "Label_2"], ["INBOX"]), seenDaysLater: 0);
        var copy = Row("f0", new(From: "SHOP@example.com "), new(["Label_2", "Label_1"], ["INBOX"]), seenDaysLater: 2);

        var finding = Run([copy, first]).ShouldHaveSingleItem();

        finding.Kind.ShouldBe(FilterFindingKind.Duplicate);
        finding.FilterIds.ShouldBe(["f1", "f0"]);
        finding.Fix.ShouldBe(new FilterFix(FilterFixKind.Delete, ["f0"]), new FixComparer());
        finding.Description.ShouldContain("Synthetic/Shop");
    }

    [Fact]
    public void Overlap_merges_the_actions_into_one_filter_and_deletes_every_member()
    {
        var a = Row("a", new(From: "shop@example.com"), new(["Label_1"], []));
        var b = Row("b", new(From: "Shop@Example.com"), new(["Label_2"], ["INBOX"]), seenDaysLater: 1);

        var finding = Run([a, b]).ShouldHaveSingleItem();

        finding.Kind.ShouldBe(FilterFindingKind.Overlap);
        finding.Fix.Kind.ShouldBe(FilterFixKind.MergeActions);
        finding.Fix.DeleteFilterIds.ShouldBe(["a", "b"]);
        finding.Fix.Create.ShouldNotBeNull().Action.AddLabelIds.ShouldBe(["Label_1", "Label_2"]);
        finding.Fix.Create.Action.RemoveLabelIds.ShouldBe(["INBOX"]);
    }

    [Fact]
    public void Overlap_keeps_a_member_that_already_has_the_union_since_gmail_refuses_an_equal_filter()
    {
        var narrow = Row("a", new(Subject: "receipt"), new(["Label_1"], []));
        var wide = Row("b", new(Subject: "Receipt"), new(["Label_1"], ["INBOX"]), seenDaysLater: 1);

        var finding = Run([narrow, wide]).ShouldHaveSingleItem();

        finding.Fix.Create.ShouldBeNull();
        finding.Fix.DeleteFilterIds.ShouldBe(["a"]);
    }

    [Fact]
    public void Deleted_label_drops_the_label_or_deletes_when_no_action_would_remain()
    {
        var partly = Row("p", new(To: "lists@example.com"), new(["Label_1", "Label_gone"], ["INBOX"]));
        var only = Row("o", new(Subject: "synthetic"), new(["Label_gone"], []), seenDaysLater: 1);

        var findings = Run([partly, only]);

        findings.Select(f => f.Kind).ShouldBe([FilterFindingKind.DeletedLabel, FilterFindingKind.DeletedLabel]);
        var drop = findings[0].Fix;
        drop.Kind.ShouldBe(FilterFixKind.DropLabel);
        drop.DeleteFilterIds.ShouldBe(["p"]);
        drop.Create.ShouldNotBeNull().Action.AddLabelIds.ShouldBe(["Label_1"]);
        drop.Create.Action.RemoveLabelIds.ShouldBe(["INBOX"]);
        drop.Create.Criteria.To.ShouldBe("lists@example.com");
        findings[1].Fix.ShouldBe(new FilterFix(FilterFixKind.Delete, ["o"]), new FixComparer());
        findings[1].Description.ShouldContain("Label_gone");
    }

    [Fact]
    public void No_recent_matches_reports_only_evaluable_filters_with_zero_matches()
    {
        var stale = Row("s", new(Subject: "old"), new(["Label_1"], []));
        var busy = Row("b", new(Subject: "new"), new(["Label_2"], []), seenDaysLater: 1);
        var unknown = Row("u", new(Query: "in:inbox"), new(["Label_2"], ["INBOX"]), seenDaysLater: 2);
        Dictionary<string, int?> counts = new() { ["s"] = 0, ["b"] = 4, ["u"] = null };

        var finding = FilterChecks.Run([stale, busy, unknown], Labels, r => counts[r.Id], 365).ShouldHaveSingleItem();

        finding.Kind.ShouldBe(FilterFindingKind.NoRecentMatches);
        finding.Fix.ShouldBe(new FilterFix(FilterFixKind.Delete, ["s"]), new FixComparer());
        finding.Description.ShouldContain("365 days");
    }

    [Fact]
    public void Mergeable_joins_single_sender_filters_with_identical_actions()
    {
        var a = Row("a", new(From: "one@example.com"), new(["Label_2"], ["INBOX"]));
        var b = Row("b", new(From: "@News.Example.com"), new(["Label_2"], ["INBOX"]), seenDaysLater: 1);
        var otherAction = Row("c", new(From: "two@example.com"), new(["Label_1"], []), seenDaysLater: 2);
        var notOnlyFrom = Row("d", new(From: "three@example.com", HasAttachment: true), new(["Label_2"], ["INBOX"]), seenDaysLater: 3);
        var list = Row("e", new(From: "x@example.com OR y@example.com"), new(["Label_2"], ["INBOX"]), seenDaysLater: 4);

        var finding = Run([a, b, otherAction, notOnlyFrom, list]).ShouldHaveSingleItem();

        finding.Kind.ShouldBe(FilterFindingKind.Mergeable);
        finding.Fix.Kind.ShouldBe(FilterFixKind.Merge);
        finding.Fix.DeleteFilterIds.ShouldBe(["a", "b"]);
        finding.Fix.Create.ShouldNotBeNull().Criteria.ShouldBe(new GmailFilterCriteria(From: "one@example.com OR @news.example.com"));
        finding.Fix.Create.Action.AddLabelIds.ShouldBe(["Label_2"]);
    }

    [Fact]
    public void Mergeable_caps_each_merged_filter_at_twenty_addresses_and_leaves_a_single_remainder()
    {
        var rows = Enumerable.Range(0, 2 * FilterChecks.MergeMaxAddresses + 1)
            .Select(i => Row(
                string.Create(CultureInfo.InvariantCulture, $"f{i:D2}"),
                new(From: string.Create(CultureInfo.InvariantCulture, $"sender{i}@example.com")),
                new(["Label_1"], []),
                seenDaysLater: i))
            .ToList();

        var findings = Run(rows);

        findings.Count.ShouldBe(2);
        findings.ShouldAllBe(f => f.Kind == FilterFindingKind.Mergeable && f.FilterIds.Count == FilterChecks.MergeMaxAddresses);
        findings[1].FilterIds[^1].ShouldBe("f39");
    }

    [Fact]
    public void A_forwarding_filter_is_never_part_of_a_fix_that_creates_a_filter()
    {
        var forward = Row("a", new(From: "one@example.com"), new(["Label_gone"], [], "fwd@example.com"));
        var plain = Row("b", new(From: "one@example.com"), new(["Label_2"], []), seenDaysLater: 1);

        Run([forward, plain]).ShouldBeEmpty();
    }

    [Fact]
    public void Later_duplicates_take_part_in_no_other_check()
    {
        var a = Row("a", new(From: "one@example.com"), new(["Label_1"], []));
        var copy = Row("b", new(From: "one@example.com"), new(["Label_1"], []), seenDaysLater: 1);
        var c = Row("c", new(From: "two@example.com"), new(["Label_1"], []), seenDaysLater: 2);

        var findings = Run([a, copy, c]);

        findings.Select(f => f.Kind).ShouldBe([FilterFindingKind.Duplicate, FilterFindingKind.Mergeable]);
        findings[1].FilterIds.ShouldBe(["a", "c"]);
    }

    private static IReadOnlyList<FilterFindingDraft> Run(IEnumerable<FilterRow> rows) => FilterChecks.Run(rows, Labels, NotEvaluable, 365);

    private static FilterRow Row(string id, GmailFilterCriteria criteria, GmailFilterAction action, int seenDaysLater = 0) => new()
    {
        Id = id,
        Criteria = FilterRow.WriteCriteria(criteria),
        Action = FilterRow.WriteAction(action),
        CriteriaSummary = FilterSnapshot.Summarise(criteria),
        FirstSeenAt = Seen.AddDays(seenDaysLater),
    };

    private sealed class FixComparer : IEqualityComparer<FilterFix>
    {
        public bool Equals(FilterFix? x, FilterFix? y) =>
            x is not null && y is not null && x.Kind == y.Kind && x.DeleteFilterIds.SequenceEqual(y.DeleteFilterIds) && x.Create == y.Create;

        public int GetHashCode(FilterFix obj) => obj.Kind.GetHashCode();
    }
}
