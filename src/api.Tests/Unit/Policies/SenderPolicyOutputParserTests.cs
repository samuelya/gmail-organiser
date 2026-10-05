using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Policies;
using GmailOrganiser.Policies.Prompts;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit.Policies;

/// <summary><see cref="SenderPolicyOutputParser"/>: a valid answer round-trips and every safety rule holds (#356).</summary>
public sealed class SenderPolicyOutputParserTests
{
    private static readonly SenderPolicyOutputParser Parser = new(new TransactionalGuard(Options.Create(new PolicyOptions
    {
        TransactionalKeywords = ["invoice", "receipt"],
    })));

    private static readonly LabelTreeIndex Tree = new(["Shop", "Shop/Example", "Types/Receipts"]);
    private static readonly SenderProfile Profile = SenderPolicyPromptTests.Profile(new CategoryMix(0, 10, 0, 5, 0, 0));

    [Fact]
    public void A_full_valid_answer_round_trips()
    {
        const string json = """
            Here you go:
            ```json
            {"topicLabel": "shop/example", "isNewLabel": true, "documentTypeLabel": "Types/Receipts", "mailType": "RECEIPT",
             "retentionDays": 365, "action": "Archive", "confidence": 0.9, "reason": "Example shop, a retailer",
             "isMixed": true, "rules": [
               {"name": "Offers", "match": {"subjectContains": "offer"}, "topicLabel": "Shop/Example/Offers",
                "mailType": "marketing", "retentionDays": 30, "action": "delete", "reason": "Ads"},
               {"match": {"listIdPresent": true, "category": "Promotions"}, "topicLabel": "Shop/Example",
                "documentTypeLabel": null, "mailType": null, "retentionDays": null, "action": "keep", "reason": "List"}
             ]}
            ```
            """;

        var parsed = Parser.Parse(json, Profile, Tree);

        parsed.Errors.ShouldBeEmpty();
        parsed.Dropped.ShouldBeEmpty();
        var p = parsed.Policy.ShouldNotBeNull();
        (p.Scope, p.ScopeKey, p.DisplayName).ShouldBe((PolicyScope.Sender, "news@example.com", "Example News"));
        p.TopicLabel.ShouldBe("Shop/Example");
        p.DocumentTypeLabel.ShouldBe("Types/Receipts");
        p.MailType.ShouldBe(MailType.Receipt);
        p.RetentionDays.ShouldBe(365);
        p.Action.ShouldBe(PolicyAction.Archive);
        p.Confidence.ShouldBe(0.9);
        p.IsMixed.ShouldBeTrue();
        p.Status.ShouldBe(PolicyStatus.Proposed);
        p.PromptVersion.ShouldBe(SenderPolicyPromptBuilder.Version);
        parsed.IsNewLabel.ShouldBeFalse();
        parsed.NewLabels.ShouldBe(["Shop/Example/Offers"]);

        // Cheapest first: header + category before subject words.
        parsed.Rules.Select(r => (r.Position, r.TopicLabel, r.Action)).ShouldBe(
        [
            (0, "Shop/Example", PolicyAction.Keep),
            (1, "Shop/Example/Offers", PolicyAction.Delete),
        ]);
        parsed.Rules[0].Match.Category.ShouldBe(MessageCategory.Promotions);
        parsed.Rules[0].Name.ShouldBe("list, category promotions");
        parsed.Rules[1].Name.ShouldBe("Offers");
        parsed.Rules[1].Source.ShouldBe(PolicyRuleSource.Llm);
        p.Rules.ShouldBe(parsed.Rules);
    }

    [Fact]
    public void Is_new_label_is_recomputed_from_the_tree()
    {
        var parsed = Parser.Parse(Single("Shop/Elsewhere", isNewLabel: false), Profile, Tree);

        parsed.IsNewLabel.ShouldBeTrue();
        Parser.Parse(Single("SHOP", isNewLabel: true), Profile, Tree).IsNewLabel.ShouldBeFalse();
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("unsubscribe")]
    public void A_mixed_default_that_removes_mail_becomes_archive(string action)
    {
        var parsed = Parser.Parse($$"""{"action":"{{action}}","confidence":0.8,"reason":"r","isMixed":true,"rules":[]}""", Profile, Tree);

        parsed.Policy!.Action.ShouldBe(PolicyAction.Archive);
        parsed.Dropped.ShouldHaveSingleItem().ShouldContain("mixed");
    }

    [Theory]
    [InlineData("subjectContains", "Your Invoice")]
    [InlineData("subjectTemplate", "receipt #")]
    public void A_delete_rule_with_a_transactional_subject_is_dropped(string field, string value)
    {
        var parsed = Parser.Parse(Mixed($$"""{"match":{"{{field}}":"{{value}}"},"topicLabel":"Shop","action":"delete","reason":"r"}"""), Profile, Tree);

        parsed.Rules.ShouldBeEmpty();
        parsed.Dropped.ShouldHaveSingleItem().ShouldContain("transactional");
    }

    [Fact]
    public void A_keep_rule_with_a_transactional_subject_stays()
    {
        Parser.Parse(Mixed("""{"match":{"subjectContains":"invoice"},"topicLabel":"Shop","action":"keep","reason":"r"}"""), Profile, Tree)
            .Rules.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("""{"match":{},"topicLabel":"Shop","action":"keep","reason":"r"}""")]
    [InlineData("""{"match":{"subjectContains":"  ","category":"nonsense"},"topicLabel":"Shop","action":"keep","reason":"r"}""")]
    [InlineData("""{"topicLabel":"Shop","action":"keep","reason":"r"}""")]
    public void A_rule_with_an_empty_match_is_dropped(string rule)
    {
        var parsed = Parser.Parse(Mixed(rule), Profile, Tree);

        parsed.Errors.ShouldBeEmpty();
        parsed.Rules.ShouldBeEmpty();
        parsed.Dropped.ShouldHaveSingleItem().ShouldContain("'match'");
    }

    [Theory]
    [InlineData("""{"match":{"category":"updates"},"action":"keep","reason":"r"}""")]
    [InlineData("""{"match":{"category":"updates"},"topicLabel":"Shop","action":"burn","reason":"r"}""")]
    public void A_rule_without_a_label_or_a_known_action_is_dropped(string rule)
    {
        var parsed = Parser.Parse(Mixed(rule), Profile, Tree);

        parsed.Rules.ShouldBeEmpty();
        parsed.Dropped.ShouldHaveSingleItem();
    }

    [Fact]
    public void Rules_beyond_eight_are_dropped()
    {
        var rules = string.Join(',', Enumerable.Range(0, 10).Select(i =>
            $$"""{"match":{"subjectContains":"topic {{i}}"},"topicLabel":"Shop","action":"archive","reason":"r"}"""));

        var parsed = Parser.Parse(Mixed(rules), Profile, Tree);

        parsed.Rules.Count.ShouldBe(SenderPolicyOutputParser.MaxRules);
        parsed.Rules[^1].Match.SubjectContains.ShouldBe("topic 7");
        parsed.Dropped.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2]")]
    [InlineData("{\"action\": ")]
    [InlineData("")]
    public void Invalid_json_is_an_error(string json)
    {
        var parsed = Parser.Parse(json, Profile, Tree);

        parsed.Policy.ShouldBeNull();
        parsed.Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("""{"action":"archive","confidence":0.8,"reason":"r","isMixed":false,"rules":[]}""", "'topicLabel'")]
    [InlineData("""{"topicLabel":"INBOX","action":"archive","confidence":0.8,"reason":"r","isMixed":false,"rules":[]}""", "'topicLabel'")]
    [InlineData("""{"topicLabel":"Shop","action":"shred","confidence":0.8,"reason":"r","isMixed":false,"rules":[]}""", "'action'")]
    [InlineData("""{"topicLabel":"Shop","action":"keep","confidence":7,"reason":"r","isMixed":false,"rules":[]}""", "'confidence'")]
    [InlineData("""{"topicLabel":"Shop","action":"keep","confidence":0.8,"isMixed":false,"rules":[]}""", "'reason'")]
    [InlineData("""{"topicLabel":"Shop","action":"keep","confidence":0.8,"reason":"r","isMixed":"yes","rules":[]}""", "'isMixed'")]
    public void A_missing_or_invalid_required_field_is_an_error(string json, string field)
    {
        var parsed = Parser.Parse(json, Profile, Tree);

        parsed.Policy.ShouldBeNull();
        parsed.Rules.ShouldBeEmpty();
        parsed.Errors.ShouldContain(e => e.Contains(field, StringComparison.Ordinal));
    }

    [Fact]
    public void An_allowlisted_sender_keeps_its_mail()
    {
        var allowlisted = SenderPolicyPromptTests.Profile(new CategoryMix(0, 10, 0, 5, 0, 0), allowlisted: true);
        var json = """
            {"topicLabel":"Shop","action":"unsubscribe","confidence":0.8,"reason":"r","isMixed":false,
             "rules":[{"match":{"category":"promotions"},"topicLabel":"Shop","action":"delete","reason":"r"}]}
            """;

        var parsed = Parser.Parse(json, allowlisted, Tree);

        parsed.Policy!.Action.ShouldBe(PolicyAction.Keep);
        parsed.Rules.ShouldHaveSingleItem().Action.ShouldBe(PolicyAction.Keep);
        parsed.Dropped.Count.ShouldBe(2);
    }

    [Fact]
    public void Optional_fields_that_are_invalid_are_dropped_not_fatal()
    {
        var json = """
            {"topicLabel":"Shop","documentTypeLabel":"Shop","mailType":"spam","retentionDays":-3,"action":"keep",
             "confidence":0.8,"reason":"r","isMixed":false,"rules":[]}
            """;

        var parsed = Parser.Parse(json, Profile, Tree);

        parsed.Errors.ShouldBeEmpty();
        var p = parsed.Policy.ShouldNotBeNull();
        (p.DocumentTypeLabel, p.MailType, p.RetentionDays).ShouldBe((null, null, null));
        parsed.Dropped.Count.ShouldBe(3);
    }

    private static string Single(string label, bool isNewLabel) =>
        $$"""{"topicLabel":"{{label}}","isNewLabel":{{(isNewLabel ? "true" : "false")}},"action":"archive","confidence":0.8,"reason":"r","isMixed":false,"rules":[]}""";

    private static string Mixed(string rules) =>
        $$"""{"action":"archive","confidence":0.8,"reason":"r","isMixed":true,"rules":[{{rules}}]}""";
}
