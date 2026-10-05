using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Policies;
using GmailOrganiser.Policies.Prompts;
using GmailOrganiser.Settings;
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
    private static readonly AppSettings Settings = new()
    {
        ActionLabelName = "Synthetic Action",
        DeleteLabelName = "Synthetic Delete",
        DocumentTypeParent = "Types",
    };

    private const string BaseRule = """{"match":{"listIdPresent":true},"topicLabel":"Shop","action":"keep","reason":"base"}""";
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

        var parsed = Parser.Parse(json, Profile, Tree, Settings);

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

        // The model's order is kept: subject words before header + category.
        parsed.Rules.Select(r => (r.Position, r.TopicLabel, r.Action)).ShouldBe(
        [
            (0, "Shop/Example/Offers", PolicyAction.Delete),
            (1, "Shop/Example", PolicyAction.Keep),
        ]);
        parsed.Rules[1].Match.Category.ShouldBe(MessageCategory.Promotions);
        parsed.Rules[1].Name.ShouldBe("list, category promotions");
        parsed.Rules[0].Name.ShouldBe("Offers");
        parsed.Rules[0].Source.ShouldBe(PolicyRuleSource.Llm);
        p.Rules.ShouldBe(parsed.Rules);
    }

    [Fact]
    public void Is_new_label_is_recomputed_from_the_tree()
    {
        var parsed = Parser.Parse(Single("Shop/Elsewhere", isNewLabel: false), Profile, Tree, Settings);

        parsed.IsNewLabel.ShouldBeTrue();
        Parser.Parse(Single("SHOP", isNewLabel: true), Profile, Tree, Settings).IsNewLabel.ShouldBeFalse();
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("unsubscribe")]
    public void A_mixed_default_that_removes_mail_becomes_archive(string action)
    {
        var parsed = Parser.Parse($$"""{"action":"{{action}}","confidence":0.8,"reason":"r","isMixed":true,"rules":[{{BaseRule}}]}""", Profile, Tree, Settings);

        parsed.Policy!.Action.ShouldBe(PolicyAction.Archive);
        parsed.Dropped.ShouldHaveSingleItem().ShouldContain("mixed");
    }

    [Theory]
    [InlineData("subjectContains", "Your Invoice", "delete")]
    [InlineData("subjectContains", "invoice", "unsubscribe")]
    public void A_rule_that_removes_mail_with_a_transactional_subject_is_dropped(string field, string value, string action)
    {
        var parsed = Parser.Parse(Mixed($$"""{"match":{"{{field}}":"{{value}}"},"topicLabel":"Shop","action":"{{action}}","reason":"r"}"""), Profile, Tree, Settings);

        parsed.Rules.ShouldHaveSingleItem().Reason.ShouldBe("base");
        parsed.Dropped.ShouldHaveSingleItem().ShouldContain("transactional");
    }

    [Fact]
    public void A_keep_rule_with_a_transactional_subject_stays()
    {
        Parser.Parse(Mixed("""{"match":{"subjectContains":"invoice"},"topicLabel":"Shop","action":"keep","reason":"r"}"""), Profile, Tree, Settings)
            .Rules.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("""{"match":{},"topicLabel":"Shop","action":"keep","reason":"r"}""")]
    [InlineData("""{"match":{"subjectContains":"  ","category":null},"topicLabel":"Shop","action":"keep","reason":"r"}""")]
    [InlineData("""{"topicLabel":"Shop","action":"keep","reason":"r"}""")]
    public void A_rule_with_an_empty_match_is_dropped(string rule)
    {
        var parsed = Parser.Parse(Mixed(rule), Profile, Tree, Settings);

        parsed.Errors.ShouldBeEmpty();
        parsed.Rules.ShouldHaveSingleItem().Reason.ShouldBe("base");
        parsed.Dropped.ShouldHaveSingleItem().ShouldContain("'match'");
    }

    [Theory]
    [InlineData("""{"match":{"category":"updates"},"action":"keep","reason":"r"}""")]
    [InlineData("""{"match":{"category":"updates"},"topicLabel":"Shop","action":"burn","reason":"r"}""")]
    public void A_rule_without_a_label_or_a_known_action_is_dropped(string rule)
    {
        var parsed = Parser.Parse(Mixed(rule), Profile, Tree, Settings);

        parsed.Rules.ShouldHaveSingleItem().Reason.ShouldBe("base");
        parsed.Dropped.ShouldHaveSingleItem();
    }

    [Fact]
    public void Rules_beyond_eight_are_dropped()
    {
        var rules = string.Join(',', Enumerable.Range(0, 2000).Select(i =>
            $$"""{"match":{"subjectContains":"topic {{i}}"},"topicLabel":"Shop","action":"archive","reason":"r"}"""));

        var parsed = Parser.Parse($$"""{"action":"archive","confidence":0.8,"reason":"r","isMixed":true,"rules":[{{rules}}]}""", Profile, Tree, Settings);

        parsed.Rules.Count.ShouldBe(SenderPolicyOutputParser.MaxRules);
        parsed.Rules[^1].Match.SubjectContains.ShouldBe("topic 7");
        parsed.Dropped.ShouldHaveSingleItem().ShouldContain("1992");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2]")]
    [InlineData("{\"action\": ")]
    [InlineData("")]
    public void Invalid_json_is_an_error(string json)
    {
        var parsed = Parser.Parse(json, Profile, Tree, Settings);

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
        var parsed = Parser.Parse(json, Profile, Tree, Settings);

        parsed.Policy.ShouldBeNull();
        parsed.Rules.ShouldBeEmpty();
        parsed.Errors.ShouldContain(e => e.Contains(field, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("unsubscribe")]
    public void A_default_that_removes_a_transactional_senders_mail_becomes_archive(string action)
    {
        var profile = Profile with { Templates = [Profile.Templates[0] with { Template = "your invoice #" }] };

        var parsed = Parser.Parse(Default(action), profile, Tree, Settings);

        parsed.Errors.ShouldBeEmpty();
        parsed.Policy!.Action.ShouldBe(PolicyAction.Archive);
        parsed.Dropped.ShouldHaveSingleItem().ShouldContain("transactional");
    }

    [Fact]
    public void A_delete_default_of_a_sender_without_transactional_subjects_is_kept()
    {
        var parsed = Parser.Parse(Default("delete"), Profile, Tree, Settings);

        parsed.Errors.ShouldBeEmpty();
        parsed.Dropped.ShouldBeEmpty();
        parsed.Policy!.Action.ShouldBe(PolicyAction.Delete);
    }

    [Fact]
    public void An_allowlisted_sender_keeps_its_mail()
    {
        var allowlisted = SenderPolicyPromptTests.Profile(new CategoryMix(0, 10, 0, 5, 0, 0), allowlisted: true);
        var json = """
            {"topicLabel":"Shop","action":"archive","confidence":0.8,"reason":"r","isMixed":true,
             "rules":[{"match":{"category":"promotions"},"topicLabel":"Shop","action":"delete","reason":"r"}]}
            """;

        var parsed = Parser.Parse(json, allowlisted, Tree, Settings);

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

        var parsed = Parser.Parse(json, Profile, Tree, Settings);

        parsed.Errors.ShouldBeEmpty();
        var p = parsed.Policy.ShouldNotBeNull();
        (p.DocumentTypeLabel, p.MailType, p.RetentionDays).ShouldBe((null, null, null));
        parsed.Dropped.Count.ShouldBe(3);
    }

    [Fact]
    public void A_sender_that_is_not_mixed_has_its_rules_dropped()
    {
        var json = """
            {"topicLabel":"Shop","action":"keep","confidence":0.8,"reason":"r","isMixed":false,
             "rules":[{"match":{"category":"promotions"},"topicLabel":"Shop","action":"delete","reason":"r"}]}
            """;

        var parsed = Parser.Parse(json, Profile, Tree, Settings);

        parsed.Policy!.Action.ShouldBe(PolicyAction.Keep);
        parsed.Policy.Rules.ShouldBeEmpty();
        parsed.Rules.ShouldBeEmpty();
        parsed.Dropped.ShouldHaveSingleItem().ShouldContain("not mixed");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"match":{},"topicLabel":"Shop","action":"keep","reason":"r"}]""")]
    public void A_mixed_sender_without_a_usable_rule_is_an_error(string rules)
    {
        var parsed = Parser.Parse($$"""{"action":"archive","confidence":0.8,"reason":"r","isMixed":true,"rules":{{rules}}}""", Profile, Tree, Settings);

        parsed.Policy.ShouldBeNull();
        parsed.Errors.ShouldHaveSingleItem().ShouldContain("'rules'");
    }

    [Theory]
    [InlineData("Synthetic Delete")]
    [InlineData("synthetic action")]
    public void The_delete_and_action_labels_are_never_topic_or_document_type_labels(string label)
    {
        Parser.Parse(Single(label, isNewLabel: false), Profile, Tree, Settings).Errors.ShouldHaveSingleItem().ShouldContain("'topicLabel'");

        var parsed = Parser.Parse(Mixed($$"""{"match":{"category":"updates"},"topicLabel":"{{label}}","action":"keep","reason":"r"}"""), Profile, Tree, Settings);
        parsed.Rules.ShouldHaveSingleItem().Reason.ShouldBe("base");

    }

    [Fact]
    public void A_delete_label_under_the_document_type_parent_is_not_a_document_type_label()
    {
        const string json = """{"topicLabel":"Shop","documentTypeLabel":"types/bin","action":"keep","confidence":0.8,"reason":"r","isMixed":false,"rules":[]}""";

        var parsed = Parser.Parse(json, Profile, Tree, Settings with { DeleteLabelName = "Types/Bin" });

        parsed.Policy!.DocumentTypeLabel.ShouldBeNull();
        parsed.Dropped.ShouldHaveSingleItem().ShouldContain("reserved");
    }

    [Theory]
    [InlineData(null, "Types/Receipts", null)]
    [InlineData("Types", "Receipts", null)]
    [InlineData("Types", "Finance/Bank", null)]
    [InlineData("Types", "types/Bills", "Types/Bills")]
    public void A_document_type_label_must_sit_under_the_parent(string? parent, string given, string? expected)
    {
        var json = $$"""
            {"topicLabel":"Shop","documentTypeLabel":"{{given}}","action":"keep","confidence":0.8,"reason":"r","isMixed":true,
             "rules":[{"match":{"category":"updates"},"topicLabel":"Shop","documentTypeLabel":"{{given}}","action":"keep","reason":"r"}]}
            """;

        var parsed = Parser.Parse(json, Profile, Tree, Settings with { DocumentTypeParent = parent });

        parsed.Policy!.DocumentTypeLabel.ShouldBe(expected);
        parsed.Rules.ShouldHaveSingleItem().DocumentTypeLabel.ShouldBe(expected);
        parsed.Dropped.Count.ShouldBe(expected is null ? 2 : 0);
    }

    [Theory]
    [InlineData("weekly offer #", "weekly offer #")]
    [InlineData("\"Weekly Offer #\"", "weekly offer #")]
    [InlineData("\"weekly off…\"", "weekly offer #")]
    [InlineData("weekly offer", null)]
    [InlineData("Weekly offer 1", null)]
    public void A_subject_template_resolves_to_a_profile_template(string given, string? expected)
    {
        var parsed = Parser.Parse(
            Mixed($$"""{"match":{"subjectTemplate":"{{given.Replace("\"", "\\\"", StringComparison.Ordinal)}}"},"topicLabel":"Shop","action":"archive","reason":"r"}"""), Profile, Tree, Settings);

        var rule = parsed.Rules.SingleOrDefault(r => r.Reason == "r");
        rule?.Match.SubjectTemplate.ShouldBe(expected);
        (rule is null).ShouldBe(expected is null);
    }

    [Theory]
    [InlineData("""{"category":"promotions","subjectContains":["sale"]}""", "subjectContains")]
    [InlineData("""{"category":"promotions","subjectContains":"LONG"}""", "subjectContains")]
    [InlineData("""{"category":"nonsense","subjectContains":"sale"}""", "category")]
    [InlineData("""{"category":"promotions","listIdPresent":"true"}""", "listIdPresent")]
    [InlineData("""{"category":"promotions","fromAddress":42}""", "fromAddress")]
    public void A_match_field_that_cannot_be_used_drops_the_whole_rule(string match, string field)
    {
        match = match.Replace("LONG", new string('a', SenderPolicyOutputParser.MaxMatchValueLength + 1), StringComparison.Ordinal);

        var parsed = Parser.Parse(Mixed($$"""{"match":{{match}},"topicLabel":"Shop","action":"delete","reason":"r"}"""), Profile, Tree, Settings);

        parsed.Errors.ShouldBeEmpty();
        parsed.Rules.ShouldHaveSingleItem().Reason.ShouldBe("base");
        parsed.Dropped.ShouldHaveSingleItem().ShouldContain($"'match.{field}'");
    }

    [Fact]
    public void The_transactional_check_sees_the_full_template_behind_a_clipped_one()
    {
        var full = "your monthly account summary for customer # is ready to view online receipt #";
        var profile = Profile with { Templates = [Profile.Templates[0] with { Template = full }] };

        var parsed = Parser.Parse(
            Mixed($$"""{"match":{"subjectTemplate":"\"{{full[..49]}}…\""},"topicLabel":"Shop","action":"delete","reason":"r"}"""), profile, Tree, Settings);

        parsed.Rules.ShouldHaveSingleItem().Reason.ShouldBe("base");
        parsed.Dropped.ShouldHaveSingleItem().ShouldContain("transactional");
    }

    [Fact]
    public void Rules_keep_the_models_order_rather_than_the_cost_order()
    {
        const string specific = """{"match":{"subjectTemplate":"weekly offer #"},"topicLabel":"Shop","action":"keep","reason":"specific"}""";
        const string broad = """{"match":{"listUnsubscribePresent":true},"topicLabel":"Shop","action":"delete","reason":"broad"}""";

        var parsed = Parser.Parse(
            $$"""{"action":"archive","confidence":0.8,"reason":"r","isMixed":true,"rules":[{{specific}},{{broad}}]}""", Profile, Tree, Settings);

        parsed.Rules.Select(r => (r.Position, r.Reason)).ShouldBe([(0, "specific"), (1, "broad")]);
    }

    private static string Single(string label, bool isNewLabel) =>
        $$"""{"topicLabel":"{{label}}","isNewLabel":{{(isNewLabel ? "true" : "false")}},"action":"archive","confidence":0.8,"reason":"r","isMixed":false,"rules":[]}""";

    private static string Default(string action) =>
        $$"""{"topicLabel":"Shop","action":"{{action}}","confidence":0.99,"reason":"r","isMixed":false}""";

    // A valid base rule first, so dropping the rule under test leaves the mixed policy usable.
    private static string Mixed(string rules) =>
        $$"""{"action":"archive","confidence":0.8,"reason":"r","isMixed":true,"rules":[{{BaseRule}},{{rules}}]}""";
}
