using GmailOrganiser.Policies;

namespace GmailOrganiser.Tests.Unit.Policies;

/// <summary>Each rule <see cref="PolicyValidation"/> enforces (#354).</summary>
public sealed class PolicyValidationTests
{
    [Fact]
    public void A_valid_policy_has_no_errors() =>
        PolicyValidation.Validate(Policy(rules: Rule())).ShouldBeEmpty();

    [Fact]
    public void Mixed_policy_cannot_delete_by_default()
    {
        var policy = Policy(rules: Rule());
        policy.IsMixed = true;
        policy.TopicLabel = null;

        PolicyValidation.Validate(policy).ShouldBeEmpty();

        policy.Action = PolicyAction.Delete;
        PolicyValidation.Validate(policy).Keys.ShouldBe(["action"]);
    }

    [Fact]
    public void Non_mixed_policy_needs_a_topic_label()
    {
        var policy = Policy();
        policy.TopicLabel = " ";

        PolicyValidation.Validate(policy).Keys.ShouldBe(["topicLabel"]);
    }

    [Fact]
    public void Rule_needs_a_match_field()
    {
        var rule = Rule();
        rule.Match = new RuleMatch { SubjectContains = "  " };

        PolicyValidation.Validate(Policy(rules: rule)).Keys.ShouldBe(["rules[0].match"]);
    }

    [Fact]
    public void At_most_twelve_rules()
    {
        PolicyValidation.Validate(Policy(rules: [.. Enumerable.Range(0, PolicyValidation.MaxRules).Select(_ => Rule())])).ShouldBeEmpty();
        PolicyValidation.Validate(Policy(rules: [.. Enumerable.Range(0, PolicyValidation.MaxRules + 1).Select(_ => Rule())]))
            .Keys.ShouldBe(["rules"]);
    }

    [Fact]
    public void Labels_are_at_most_225_characters()
    {
        var policy = Policy(rules: Rule());
        policy.TopicLabel = new string('a', 225);
        PolicyValidation.Validate(policy).ShouldBeEmpty();

        policy.TopicLabel = new string('a', 226);
        policy.Rules[0].DocumentTypeLabel = new string('b', 226);
        PolicyValidation.Validate(policy).Keys.ShouldBe(["topicLabel", "rules[0].documentTypeLabel"], ignoreOrder: true);
    }

    [Fact]
    public void Labels_are_at_most_five_levels_deep()
    {
        var policy = Policy(rules: Rule());
        policy.DocumentTypeLabel = "a/b/c/d/e";
        PolicyValidation.Validate(policy).ShouldBeEmpty();

        policy.DocumentTypeLabel = "a/b/c/d/e/f";
        policy.Rules[0].TopicLabel = "a/b/c/d/e/f";
        PolicyValidation.Validate(policy).Keys.ShouldBe(["documentTypeLabel", "rules[0].topicLabel"], ignoreOrder: true);
    }

    private static SenderPolicyRow Policy(params SenderPolicyRuleRow[] rules) => new()
    {
        Id = Guid.NewGuid(),
        Scope = PolicyScope.Domain,
        ScopeKey = "example.com",
        TopicLabel = "Shops/Example",
        Action = PolicyAction.Archive,
        Status = PolicyStatus.Proposed,
        Rules = [.. rules],
    };

    private static SenderPolicyRuleRow Rule() => new()
    {
        Id = Guid.NewGuid(),
        Name = "promotions",
        Match = new RuleMatch { ListIdPresent = true },
        TopicLabel = "Shops/Example",
        Action = PolicyAction.Delete,
        Status = PolicyStatus.Approved,
        Source = PolicyRuleSource.Llm,
    };
}
