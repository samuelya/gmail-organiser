using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Policies;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit.Policies;

/// <summary>The transactional guard and every branch of the policy matcher (#354).</summary>
public sealed class PolicyMatcherTests
{
    private static readonly TransactionalGuard Guard = new(Options.Create(new PolicyOptions
    {
        TransactionalKeywords = ["invoice", "due", "order confirmation"],
    }));

    private static readonly PolicyMatcher Matcher = new(Guard);

    [Theory]
    [InlineData("Your INVOICE is ready", null, true)]
    [InlineData("Weekly news", "payment due tomorrow", true)]
    [InlineData("Your order confirmation", null, true)]
    [InlineData("Overdue? Not this one", "subdued colours", false)]
    [InlineData("Invoices and more", null, false)]
    [InlineData("Weekly news", null, false)]
    [InlineData(null, null, false)]
    public void Guard_matches_whole_keywords_in_subject_or_snippet(string? subject, string? snippet, bool expected) =>
        Guard.IsTransactional(Message(subject: subject, snippet: snippet)).ShouldBe(expected);

    [Fact]
    public void Guard_hits_on_an_attachment()
    {
        Guard.IsTransactional(Message(hasAttachment: true)).ShouldBeTrue();
        new TransactionalGuard(Options.Create(new PolicyOptions())).IsTransactional(Message(subject: "invoice")).ShouldBeFalse();
    }

    [Fact]
    public void Non_mixed_policy_without_rules_gives_its_default()
    {
        var match = Matcher.Match(Message(), Policy(PolicyAction.Delete)).ShouldNotBeNull();

        match.Rule.ShouldBeNull();
        match.TopicLabel.ShouldBe("Shops/Example");
        match.Action.ShouldBe(PolicyAction.Delete);
        match.Guarded.ShouldBeFalse();
    }

    [Fact]
    public void Guard_downgrades_a_non_mixed_delete_to_archive()
    {
        var match = Matcher.Match(Message(subject: "Invoice 42"), Policy(PolicyAction.Delete)).ShouldNotBeNull();

        match.Action.ShouldBe(PolicyAction.Archive);
        match.Guarded.ShouldBeTrue();
    }

    [Fact]
    public void Guard_beats_a_delete_rule()
    {
        var policy = Policy(PolicyAction.Keep, Rule(PolicyAction.Delete, new RuleMatch { ListIdPresent = true }));

        Matcher.Match(Message(listId: "news.example.com", subject: "Invoice 42"), policy).ShouldNotBeNull()
            .ShouldSatisfyAllConditions(m => m.Rule.ShouldBeNull(), m => m.Action.ShouldBe(PolicyAction.Keep), m => m.Guarded.ShouldBeTrue());

        policy.IsMixed = true;
        Matcher.Match(Message(listId: "news.example.com", subject: "Invoice 42"), policy).ShouldBeNull();
    }

    [Fact]
    public void Guard_does_not_override_a_non_delete_rule()
    {
        var rule = Rule(PolicyAction.Archive, new RuleMatch { SubjectContains = "invoice" }, topic: "Shops/Example/Invoices");
        var policy = Mixed(rule);

        var match = Matcher.Match(Message(subject: "Your invoice"), policy).ShouldNotBeNull();

        match.Rule.ShouldBe(rule);
        match.TopicLabel.ShouldBe("Shops/Example/Invoices");
        match.Guarded.ShouldBeFalse();
    }

    [Fact]
    public void Mixed_policy_with_no_matching_rule_gives_null()
    {
        var policy = Mixed(Rule(PolicyAction.Delete, new RuleMatch { Category = MessageCategory.Promotions }));

        Matcher.Match(Message(category: MessageCategory.Updates), policy).ShouldBeNull();
        Matcher.Match(Message(category: MessageCategory.Promotions), policy).ShouldNotBeNull().Action.ShouldBe(PolicyAction.Delete);
    }

    [Theory]
    [InlineData(PolicyStatus.Proposed)]
    [InlineData(PolicyStatus.Rejected)]
    public void Unapproved_rules_never_match(PolicyStatus status)
    {
        var rule = Rule(PolicyAction.Delete, new RuleMatch { ListIdPresent = true });
        rule.Status = status;

        Matcher.Match(Message(listId: "news.example.com"), Mixed(rule)).ShouldBeNull();
    }

    [Fact]
    public void Every_set_field_must_hold()
    {
        var rule = Rule(PolicyAction.Archive, new RuleMatch
        {
            ListUnsubscribePresent = true,
            FromSubdomain = "example.com",
            SubjectTemplate = "your order # shipped",
        });
        var policy = Mixed(rule);

        Matcher.Match(Message(from: "a@mail.example.com", unsubscribe: "<mailto:u@example.com>", subject: "Your order 1234 shipped"), policy)
            .ShouldNotBeNull().Rule.ShouldBe(rule);
        Matcher.Match(Message(from: "a@mail.example.com", subject: "Your order 1234 shipped"), policy).ShouldBeNull();
        Matcher.Match(Message(from: "a@badexample.com", unsubscribe: "<mailto:u@example.com>", subject: "Your order 1234 shipped"), policy).ShouldBeNull();
        Matcher.Match(Message(from: "a@mail.example.com", unsubscribe: "<mailto:u@example.com>", subject: "Your order arrived"), policy).ShouldBeNull();
    }

    [Fact]
    public void From_address_matches_the_canonical_or_the_raw_address()
    {
        var policy = Mixed(Rule(PolicyAction.Archive, new RuleMatch { FromAddress = "Shop@Example.com" }));

        Matcher.Match(Message(from: "shop@example.com"), policy).ShouldNotBeNull();
        Matcher.Match(Message(from: "relay-1@relay.example.org"), policy, canonicalAddress: "shop@example.com").ShouldNotBeNull();
        Matcher.Match(Message(from: "other@example.com"), policy).ShouldBeNull();
    }

    [Fact]
    public void Cheaper_rule_wins_over_an_earlier_dearer_one()
    {
        var bySubject = Rule(PolicyAction.Keep, new RuleMatch { SubjectContains = "sale" }, position: 0);
        var byHeader = Rule(PolicyAction.Archive, new RuleMatch { ListIdPresent = true }, position: 1);

        Matcher.Match(Message(listId: "news.example.com", subject: "Big sale"), Mixed(bySubject, byHeader))
            .ShouldNotBeNull().Rule.ShouldBe(byHeader);
    }

    [Fact]
    public void Cost_order_is_stable_by_class()
    {
        var contains = Rule(PolicyAction.Keep, new RuleMatch { SubjectContains = "a" }, name: "contains");
        var template = Rule(PolicyAction.Keep, new RuleMatch { SubjectTemplate = "a #" }, name: "template");
        var category = Rule(PolicyAction.Keep, new RuleMatch { Category = MessageCategory.Social }, name: "category");
        var subdomain = Rule(PolicyAction.Keep, new RuleMatch { FromSubdomain = "example.com" }, name: "subdomain");
        var address = Rule(PolicyAction.Keep, new RuleMatch { FromAddress = "a@example.com" }, name: "address");
        var header = Rule(PolicyAction.Keep, new RuleMatch { ListIdPresent = false }, name: "header");
        var headerAndContains = Rule(PolicyAction.Keep, new RuleMatch { ListIdPresent = true, SubjectContains = "b" }, name: "header+contains");

        PolicyMatcher.CostOrder([contains, headerAndContains, template, category, subdomain, address, header])
            .Select(r => r.Name)
            .ShouldBe(["header", "subdomain", "address", "category", "template", "contains", "header+contains"]);
    }

    private static MessageRow Message(
        string from = "shop@example.com",
        string? subject = "Weekly news",
        string? snippet = null,
        string? listId = null,
        string? unsubscribe = null,
        MessageCategory? category = null,
        bool hasAttachment = false) => new()
        {
            Id = "msg-1",
            ThreadId = "thread-1",
            FromAddress = from,
            Subject = subject,
            Snippet = snippet,
            ListId = listId,
            ListUnsubscribe = unsubscribe,
            Category = category,
            HasAttachment = hasAttachment,
        };

    private static SenderPolicyRow Policy(PolicyAction action, params SenderPolicyRuleRow[] rules) => new()
    {
        Id = Guid.NewGuid(),
        Scope = PolicyScope.Sender,
        ScopeKey = "shop@example.com",
        TopicLabel = "Shops/Example",
        MailType = MailType.Marketing,
        Action = action,
        Status = PolicyStatus.Approved,
        Rules = [.. rules],
    };

    private static SenderPolicyRow Mixed(params SenderPolicyRuleRow[] rules)
    {
        var policy = Policy(PolicyAction.Keep, rules);
        policy.IsMixed = true;
        policy.TopicLabel = null;
        return policy;
    }

    private static SenderPolicyRuleRow Rule(
        PolicyAction action, RuleMatch match, string topic = "Shops/Example", int position = 0, string name = "rule") => new()
        {
            Id = Guid.NewGuid(),
            Position = position,
            Name = name,
            Match = match,
            TopicLabel = topic,
            Action = action,
            Status = PolicyStatus.Approved,
            Source = PolicyRuleSource.Llm,
        };
}
