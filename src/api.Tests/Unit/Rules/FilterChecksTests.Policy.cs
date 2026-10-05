using GmailOrganiser.Gmail;
using GmailOrganiser.Rules;
using GmailOrganiser.Rules.Review;

namespace GmailOrganiser.Tests.Unit.Rules;

/// <summary>The consolidation checks against the policy proposals (#374, #446, #451, #454).</summary>
public sealed partial class FilterChecksTests
{
    // A skip-inbox proposal carves transactional mail out for policy auto-apply; that carve-out is no requirement on the
    // filter, but a filter whose own conditions sit inside it is not within the proposal.
    [Fact]
    public void A_filter_is_within_a_skip_inbox_proposal_unless_its_own_conditions_are_in_the_transactional_carve_out()
    {
        const string negation = "-has:attachment -invoice -\"two words\"";
        var proposal = Proposal("a@example.com", negation, "Synthetic/Shop", skipInbox: true);
        var labels = Labels.Append(new("TRASH", "TRASH", GmailLabelType.System)).ToList();
        FilterRow[] rows =
        [
            Row("archives", new(From: "a@example.com"), new(["Label_1"], ["INBOX"])),
            Row("labels", new(From: "a@example.com"), new(["Label_1"], [])),
            Row("other", new(From: "a@example.com"), new(["Label_2"], [])),
            Row("trash", new(From: "a@example.com"), new(["TRASH"], [])),
            Row("attachments", new(From: "a@example.com", HasAttachment: true), new(["Label_2"], [])),
            Row("invoices", new(From: "a@example.com", Query: "invoice"), new(["Label_2"], [])),
        ];

        var findings = PolicyFindings(FilterChecks.Run(rows, labels, NotEvaluable, 365, [proposal]));

        findings.Select(f => (f.Kind, string.Join(',', f.FilterIds)))
            .ShouldBe([(FilterFindingKind.PolicyConflict, "other"), (FilterFindingKind.PolicyConflict, "trash"), (FilterFindingKind.OverlapsPolicy, "archives")]);
        findings.ShouldAllBe(f => f.Fix.PolicyId == proposal.PolicyId && f.Fix.Create!.Criteria.Query == negation && f.Fix.Create.Action.RemoveLabelIds.SequenceEqual(new[] { "INBOX" }));
    }

    // Every exclusion is its own term; an excluded sender's filter belongs to that sender's own policy, found there.
    [Fact]
    public void A_proposal_excluding_two_senders_judges_the_other_filters_and_leaves_the_excluded_senders_to_their_own_policy()
    {
        var domain = Proposal("@example.com", "-from:x@example.com -from:y@example.com", "Synthetic/Shop");
        var own = Proposal("x@example.com", null, "Synthetic/News");
        var list = Proposal(null, "list:news.example.com -from:x@example.com", "Synthetic/Shop");
        FilterRow[] rows =
        [
            Row("in", new(From: "a@example.com"), new(["Label_1"], [])),
            Row("wrong", new(From: "b@example.com"), new(["Label_2"], [])),
            Row("x", new(From: "x@example.com"), new(["Label_1"], [])),
            Row("y", new(From: "y@example.com"), new(["Label_2"], [])),
            Row("list", new(Query: "list:<news.example.com>"), new(["Label_1"], [])),
            Row("list-x", new(Query: "list:<news.example.com> from:x@example.com"), new(["Label_1"], [])),
        ];

        var findings = PolicyFindings(FilterChecks.Run(rows, Labels, NotEvaluable, 365, [domain, own, list]));

        findings.Select(f => (f.Kind, string.Join(',', f.FilterIds), f.Fix.PolicyId)).ShouldBe(
        [
            (FilterFindingKind.PolicyConflict, "wrong", domain.PolicyId),
            (FilterFindingKind.PolicyConflict, "x", own.PolicyId),
            (FilterFindingKind.OverlapsPolicy, "in", domain.PolicyId),
            (FilterFindingKind.OverlapsPolicy, "list", list.PolicyId),
        ]);
        findings.Single(f => f.FilterIds[0] == "in").Fix.Create!.Criteria.Query.ShouldBe("-from:x@example.com -from:y@example.com");
    }

    [Fact]
    public void A_relabel_names_the_filters_other_actions_the_policy_filter_drops()
    {
        var labels = Labels.Concat(new[] { "TRASH", "STARRED", "IMPORTANT", "SPAM" }.Select(id => new GmailLabel(id, id, GmailLabelType.System))).ToList();
        FilterRow[] rows =
        [
            Row("starred", new(From: "a@example.com"), new(["Label_2", "STARRED", "IMPORTANT"], ["UNREAD", "SPAM"])),
            Row("trash", new(From: "b@example.com"), new(["TRASH", "Label_2"], ["INBOX"])),
            Row("plain", new(From: "c@example.com"), new(["Label_2"], [])),
        ];

        var findings = PolicyFindings(FilterChecks.Run(rows, labels, NotEvaluable, 365, [DomainProposal()]));

        findings.Select(f => f.Kind).ShouldAllBe(k => k == FilterFindingKind.PolicyConflict);
        var starred = findings.Single(f => f.FilterIds[0] == "starred").Description;
        starred.ShouldContain("adds Synthetic/News");
        starred.ShouldContain("won't star, mark important, mark read and never send to spam, as this one does; those actions are dropped");
        starred.ShouldNotContain("STARRED");
        var trash = findings.Single(f => f.FilterIds[0] == "trash").Description;
        trash.ShouldContain("sends to Trash or Spam");
        trash.ShouldContain("won't add Synthetic/News, as this one does");
        trash.ShouldNotContain("INBOX");
        findings.Single(f => f.FilterIds[0] == "plain").Description.ShouldNotContain("dropped");
        findings.ShouldAllBe(f => f.Fix.Create!.Action.AddLabelIds.SequenceEqual(new[] { "Label_1" }) && f.Fix.Create.Action.RemoveLabelIds.Count == 0);
    }

    // #454: which of a filter's mail is in a carve-out can't be told from its tokens (subject:invoice is all invoices,
    // filename:pdf all attachments, from:a@sub.example.com all excluded senders), so against a proposal with a negated
    // token only a filter with nothing beyond the proposal's own positive conditions is judged, whatever its action.
    [Theory]
    [InlineData("a@example.com", "subject:invoice", null, false)]
    [InlineData("a@example.com", null, "invoice", false)]
    [InlineData("a@example.com", "filename:pdf", null, false)]
    [InlineData("a@example.com", "list:<news.example.com>", null, false)]
    [InlineData("a@example.com", null, null, true)]
    [InlineData("a@example.com", "   ", null, true)]
    public void Against_a_skip_inbox_proposal_only_a_sender_only_filter_is_judged(string from, string? query, string? subject, bool judged)
    {
        var proposal = Proposal("a@example.com", "-has:attachment -invoice -\"two words\"", "Synthetic/Shop", skipInbox: true);
        var labels = Labels.Append(new("TRASH", "TRASH", GmailLabelType.System)).ToList();
        FilterRow[] rows =
        [
            Row("same", new(From: from, Query: query, Subject: subject), new(["Label_1"], ["INBOX"])),
            Row("other", new(From: from, Query: query, Subject: subject), new(["Label_2"], [])),
            Row("trash", new(From: from, Query: query, Subject: subject), new(["TRASH"], [])),
        ];

        var findings = PolicyFindings(FilterChecks.Run(rows, labels, NotEvaluable, 365, [proposal]));

        findings.Select(f => (f.Kind, f.FilterIds[0])).ShouldBe(judged
            ? [(FilterFindingKind.PolicyConflict, "other"), (FilterFindingKind.PolicyConflict, "trash"), (FilterFindingKind.OverlapsPolicy, "same")]
            : []);
    }

    [Theory]
    [InlineData(null, "list:<news.example.com> from:a@sub.example.com", false)]
    [InlineData(null, "list:<news.example.com> subject:invoice", false)]
    [InlineData("a@example.com", "list:<news.example.com>", false)]
    [InlineData(null, "list:<News.example.com>", true)]
    public void Against_a_list_proposal_excluding_a_subdomain_only_the_bare_list_filter_is_judged(string? from, string query, bool judged)
    {
        var proposal = Proposal(null, "list:news.example.com -from:@sub.example.com", "Synthetic/Shop");
        FilterRow[] rows =
        [
            Row("same", new(From: from, Query: query), new(["Label_1"], [])),
            Row("other", new(From: from, Query: query), new(["Label_2"], [])),
        ];

        var findings = PolicyFindings(FilterChecks.Run(rows, Labels, NotEvaluable, 365, [proposal]));

        findings.Select(f => (f.Kind, f.FilterIds[0])).ShouldBe(judged
            ? [(FilterFindingKind.PolicyConflict, "other"), (FilterFindingKind.OverlapsPolicy, "same")]
            : []);
    }

    // A sender-only filter under a -from: exclusion is matched subdomain-aware, like a covering @domain.
    [Theory]
    [InlineData("a@example.com", true)]
    [InlineData("a@other.example.com", true)]
    [InlineData("a@sub.example.com", false)]
    [InlineData("a@deep.sub.example.com", false)]
    [InlineData("x@example.com", false)]
    public void A_sender_only_filter_under_a_domain_proposal_is_judged_unless_an_exclusion_covers_its_sender(string from, bool judged)
    {
        var proposal = Proposal("@example.com", "-from:x@example.com -from:@sub.example.com", "Synthetic/Shop");
        var rows = new[] { Row("other", new(From: from), new(["Label_2"], [])) };

        var findings = PolicyFindings(FilterChecks.Run(rows, Labels, NotEvaluable, 365, [proposal]));

        findings.Select(f => f.Kind).ShouldBe(judged ? [FilterFindingKind.PolicyConflict] : []);
    }
}
