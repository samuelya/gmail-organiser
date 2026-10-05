using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Policies;
using GmailOrganiser.Tests.Fakes;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// Which active filters cover a policy proposal (#373, review round): only one with the same criteria, compared after
/// normalising; and the first-match exclusions of a mixed policy's rules.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PolicyFilterProposalCoverageTests(ApiFactory factory, PostgresFixture postgres)
    : PolicyFilterProposalTestBase(factory, postgres), IClassFixture<ApiFactory>
{
    [Fact]
    public async Task An_active_filter_covers_a_proposal_only_with_the_same_criteria_whatever_their_case_order_or_brackets()
    {
        await using (var db = Postgres.CreateDbContext())
        {
            db.Senders.AddRange(Sender("news@example.com", "news@example.com"), Sender("sub@example.org", "sub@example.org"));
            db.SenderPolicies.AddRange(
                Policy(PolicyScope.Sender, "news@example.com", PolicyAction.Keep, "Synthetic/News"),
                Policy(PolicyScope.Sender, "sub@example.org", PolicyAction.Keep, "Synthetic/Sub"),
                Policy(PolicyScope.Domain, "example.org", PolicyAction.Keep, "Synthetic/Org"));

            // A rule-like filter on the sender, and the accepted domain proposal with its narrower-policy exclusion.
            db.Filters.AddRange(
                Filter("rule", new GmailFilterCriteria(From: "news@example.com", Query: "subject:\"Weekly\"")),
                Filter("domain", new GmailFilterCriteria(From: "@example.org", Query: "-from:sub@example.org")));
            await db.SaveChangesAsync(Ct);
        }

        var items = (await ProposalsAsync("policy")).Items;

        // The domain filter covers the domain policy only, not the sender under it; the rule filter covers nothing.
        items.Select(p => p.SenderAddress).ShouldBe(["news@example.com", "sub@example.org"], ignoreOrder: true);

        await using (var db = Postgres.CreateDbContext())
        {
            db.Filters.Add(Filter("same", new GmailFilterCriteria(From: "(News@Example.com)")));
            await db.SaveChangesAsync(Ct);
        }

        (await ProposalsAsync("policy")).Items.ShouldHaveSingleItem().SenderAddress.ShouldBe("sub@example.org");
    }

    [Fact]
    public async Task A_rule_filter_negates_a_multi_condition_rule_above_it_as_a_group_and_is_partial_after_an_untestable_one()
    {
        await using (var db = Postgres.CreateDbContext())
        {
            db.Senders.Add(Sender("promo@example.com", "promo@example.com"));
            var policy = Policy(PolicyScope.Sender, "promo@example.com", PolicyAction.Archive, null);
            policy.IsMixed = true;
            policy.Rules =
            [
                Rule(policy.Id, 0, new RuleMatch { FromSubdomain = "mail.example.com", Category = MessageCategory.Promotions }, PolicyAction.Keep),
                Rule(policy.Id, 1, new RuleMatch { ListUnsubscribePresent = true }, PolicyAction.Archive),
                Rule(policy.Id, 2, new RuleMatch { Category = MessageCategory.Updates }, PolicyAction.Archive),
            ];
            db.SenderPolicies.Add(policy);
            await db.SaveChangesAsync(Ct);
        }

        var items = (await ProposalsAsync("policy")).Items.OrderBy(p => p.DisplayName, StringComparer.Ordinal).ToList();

        items.Select(p => p.DisplayName).ShouldBe(["Synthetic rule 0", "Synthetic rule 2"]);
        items[0].Suggested.Criteria.Query.ShouldBe("from:@mail.example.com category:promotions");
        items[0].Partial.ShouldBeFalse();
        items[1].Suggested.Criteria.Query.ShouldBe("category:updates -(from:@mail.example.com category:promotions)");
        items[1].Partial.ShouldBeTrue();
        items[1].Note.ShouldNotBeNull().ShouldContain("Also matches the mail of \"Synthetic rule 1\" above it, which Gmail filters can't test");
        items.ShouldAllBe(p => !p.Suggested.Action.SkipInbox && p.Pattern.ToBeDeleted == false);
    }

    [Fact]
    public async Task A_rule_filter_whose_exclusions_do_not_fit_the_cap_is_not_proposed()
    {
        // Rule 0's query is just under the cap (FilterCriteriaLimits.MaxQueryChars); rule 1's, with rule 0 negated, is over it.
        var longText = new string('a', 1460);
        await using (var db = Postgres.CreateDbContext())
        {
            db.Senders.Add(Sender("promo@example.com", "promo@example.com"));
            var policy = Policy(PolicyScope.Sender, "promo@example.com", PolicyAction.Archive, null);
            policy.IsMixed = true;
            policy.Rules =
            [
                Rule(policy.Id, 0, new RuleMatch { SubjectContains = longText }, PolicyAction.Keep),
                Rule(policy.Id, 1, new RuleMatch { SubjectContains = "Daily deal" }, PolicyAction.Archive),
            ];
            db.SenderPolicies.Add(policy);
            await db.SaveChangesAsync(Ct);
        }

        var proposal = (await ProposalsAsync("policy")).Items.ShouldHaveSingleItem();

        // Rule 0 fits on its own; rule 1 with rule 0 negated would not, and a wider filter is never proposed instead.
        proposal.DisplayName.ShouldBe("Synthetic rule 0");
        proposal.Suggested.Criteria.Query.ShouldBe($"subject:\"{longText}\"");
    }
}
