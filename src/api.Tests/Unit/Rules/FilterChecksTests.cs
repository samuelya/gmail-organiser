using System.Globalization;
using GmailOrganiser.Gmail;
using GmailOrganiser.Review;
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

    [Fact]
    public void No_recent_matches_skips_filters_that_trash_spam_or_forward()
    {
        var trash = Row("t", new(Subject: "a"), new(["TRASH"], []));
        var spam = Row("s", new(Subject: "b"), new(["SPAM"], []), seenDaysLater: 1);
        var forward = Row("f", new(Subject: "c"), new([], [], "fwd@example.com"), seenDaysLater: 2);
        var stale = Row("x", new(Subject: "d"), new(["Label_1"], []), seenDaysLater: 3);

        var labels = Labels.Concat([new("TRASH", "TRASH", GmailLabelType.System), new("SPAM", "SPAM", GmailLabelType.System)]).ToList();

        var finding = FilterChecks.Run([trash, spam, forward, stale], labels, _ => 0, 365).ShouldHaveSingleItem();

        finding.FilterIds.ShouldBe(["x"]);
    }

    [Fact]
    public void A_duplicate_forwarding_filter_is_reported_without_a_fix()
    {
        var first = Row("a", new(From: "one@example.com"), new([], [], "fwd@example.com"));
        var copy = Row("b", new(From: "one@example.com"), new([], [], "FWD@example.com"), seenDaysLater: 1);

        var finding = FilterChecks.Run([first, copy], Labels, _ => 0, 365).ShouldHaveSingleItem();

        finding.Kind.ShouldBe(FilterFindingKind.Duplicate);
        finding.Fix.ShouldBe(new FilterFix(FilterFixKind.None, []), new FixComparer());
    }

    [Fact]
    public void An_overlap_whose_union_adds_and_removes_one_label_is_reported_without_a_fix()
    {
        var star = Row("a", new(Subject: "receipt"), new(["STARRED"], []));
        var unstar = Row("b", new(Subject: "receipt"), new(["Label_1"], ["STARRED"]), seenDaysLater: 1);
        var labels = Labels.Append(new("STARRED", "STARRED", GmailLabelType.System)).ToList();

        var finding = FilterChecks.Run([star, unstar], labels, NotEvaluable, 365).ShouldHaveSingleItem();

        finding.Kind.ShouldBe(FilterFindingKind.Overlap);
        finding.Fix.ShouldBe(new FilterFix(FilterFixKind.None, []), new FixComparer());
        finding.Description.ShouldContain("STARRED");
    }

    [Fact]
    public void From_only_terms_are_null_unless_the_criteria_is_only_a_from_list()
    {
        FilterChecks.FromOnlyTerms(new(From: " One@Example.com OR @news.example.com ")).ShouldBe(["one@example.com", "@news.example.com"]);
        FilterChecks.FromOnlyTerms(new(From: "one@example.com", HasAttachment: false)).ShouldBe(["one@example.com"]);
        FilterChecks.FromOnlyTerms(new(From: "one@example.com", Subject: "x")).ShouldBeNull();
        FilterChecks.FromOnlyTerms(new(From: "not an address")).ShouldBeNull();
        FilterChecks.FromOnlyTerms(new(Subject: "x")).ShouldBeNull();
    }

    [Fact]
    public void A_filter_is_within_a_domain_proposal_only_for_senders_it_does_not_exclude_and_without_an_or_query()
    {
        var policy = Guid.NewGuid();
        var proposal = new FilterProposalDto(
            "example.com", null, 1, null, new SenderPatternDto("Synthetic/Shop", false, false, 0, 1, 0, null),
            new FilterSuggestionDto(
                new FilterCriteriaDto("@example.com", null, null, "-from:own@example.com", null, null, null, null, null),
                new FilterActionRequest(["Synthetic/Shop"], false, false)),
            "policy:1", FilterProposalSources.Policy, policy);
        var label = new GmailFilterAction(["Label_1"], []);
        FilterRow[] rows =
        [
            Row("in", new(From: "one@example.com"), label),
            Row("sub", new(From: "two@news.example.com", Subject: "Weekly"), new(["Label_2"], [])),
            Row("excluded", new(From: "own@example.com"), label),
            Row("wider", new(From: "three@example.com", Query: "a OR b"), label),
            Row("other", new(From: "one@example.org"), new(["Label_2"], [])),
        ];

        var findings = FilterChecks.Run(rows, Labels, NotEvaluable, 365, [proposal])
            .Where(f => f.Kind is FilterFindingKind.OverlapsPolicy or FilterFindingKind.PolicyConflict).ToList();

        findings.Select(f => (f.Kind, string.Join(',', f.FilterIds), f.Fix.PolicyId))
            .ShouldBe([(FilterFindingKind.PolicyConflict, "sub", policy), (FilterFindingKind.OverlapsPolicy, "in", policy)]);
        findings.ShouldAllBe(f => f.Fix.Create!.Criteria.From == "@example.com" && f.Fix.Create.Action.AddLabelIds.SequenceEqual(new[] { "Label_1" }));
        FilterChecks.Run(rows, Labels, NotEvaluable, 365, [proposal with { Suggested = proposal.Suggested with { Action = new(["Synthetic/Missing"], false, false) } }])
            .ShouldNotContain(f => f.Kind == FilterFindingKind.OverlapsPolicy || f.Kind == FilterFindingKind.PolicyConflict);
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
