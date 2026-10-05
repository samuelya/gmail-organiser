using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Policies;
using GmailOrganiser.Policies.Prompts;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit.Policies;

/// <summary><see cref="SenderPolicyPromptBuilder"/> and the fake responder's policy answer (#356).</summary>
public sealed class SenderPolicyPromptTests
{
    private static readonly AppSettings Settings = new()
    {
        ActionLabelName = "Synthetic Action",
        DeleteLabelName = "Synthetic Delete",
        DocumentTypeParent = "Synthetic Types",
    };

    private static readonly SenderPolicyOutputParser Parser = new(new TransactionalGuard(Options.Create(new PolicyOptions
    {
        TransactionalKeywords = ["invoice"],
    })));

    [Fact]
    public void Fills_every_placeholder_and_keeps_the_profile_out_of_the_system_message()
    {
        var profile = Profile(new CategoryMix(0, 10, 0, 0, 0, 0)) with
        {
            ApprovedPolicyHints = [new PolicyHint(PolicyScope.Sender, "billing@example.com", false, "Shop", null, PolicyAction.Archive)],
        };

        var prompt = SenderPolicyPromptBuilder.Build(profile, ["Shop", "Synthetic Types/Bills"], Settings);

        prompt.PromptVersion.ShouldBe("sender-policy-v1");
        prompt.Prompt.ShouldNotContain("{{");
        prompt.System.ShouldStartWith(SenderPolicyPromptBuilder.Marker);
        prompt.System.ShouldContain("- `action_bill`: ");
        prompt.System.ShouldContain("- `security_otp`: ");
        prompt.System.ShouldContain("Synthetic Action");
        prompt.System.ShouldContain("Synthetic Delete");
        prompt.System.ShouldContain("`Synthetic Types/Bills`");
        prompt.System.ShouldNotContain("news@example.com");
        prompt.User.ShouldContain("approved_policies_same_domain: sender billing@example.com -> Shop archive");
        prompt.User.Split("billing@example.com").Length.ShouldBe(2);
        prompt.User.ShouldContain(SenderPolicyPromptBuilder.ProfileHeading + "\nscope: sender news@example.com");
        prompt.Messages.Select(m => m.Role).ShouldBe([ChatRole.System, ChatRole.User]);
    }

    [Fact]
    public void Options_are_schema_constrained_at_temperature_zero_with_num_ctx()
    {
        var options = SenderPolicyPromptBuilder.CreateOptions(8192);

        var schema = options.ResponseFormat.ShouldBeOfType<ChatResponseFormatJson>().Schema!.Value;
        var rule = schema.GetProperty("properties").GetProperty("rules").GetProperty("items").GetProperty("properties");
        schema.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["keep", "archive", "delete", "unsubscribe"]);
        rule.GetProperty("mailType").GetProperty("enum").EnumerateArray().ShouldContain(e => e.GetString() == "action_bill");
        rule.GetProperty("match").GetProperty("properties").GetProperty("category").GetProperty("enum").GetArrayLength().ShouldBe(6);
        options.Temperature.ShouldBe(0);
        options.AdditionalProperties![LlmCallMeter.NumCtxKey].ShouldBe(8192);
    }

    [Fact]
    public void Fake_answers_a_single_category_sender_with_an_archive_policy_that_parses()
    {
        var profile = Profile(new CategoryMix(0, 0, 0, 12, 0, 3));
        var prompt = SenderPolicyPromptBuilder.Build(profile, ["Updates/Example"], Settings);

        var parsed = Parser.Parse(FakeAnalysisResponder.Answer(prompt.Messages.ToList()), profile, new LabelTreeIndex(["Updates/Example"]), Settings);

        parsed.Errors.ShouldBeEmpty();
        parsed.Policy.ShouldNotBeNull();
        parsed.Policy.IsMixed.ShouldBeFalse();
        parsed.Policy.TopicLabel.ShouldBe("Updates/Example");
        parsed.Policy.Action.ShouldBe(PolicyAction.Archive);
        parsed.IsNewLabel.ShouldBeFalse();
        parsed.Rules.ShouldBeEmpty();
    }

    [Fact]
    public void Fake_answers_a_multi_category_sender_with_a_mixed_policy_and_one_rule_per_category()
    {
        var profile = Profile(new CategoryMix(0, 0, 0, 5, 0, 1) with { Promotions = 20 });
        var prompt = SenderPolicyPromptBuilder.Build(profile, [], Settings);

        var answer = FakeAnalysisResponder.Answer(prompt.Messages.ToList());
        var parsed = Parser.Parse(answer, profile, LabelTreeIndex.Empty, Settings);

        answer.ShouldBe(FakeAnalysisResponder.Answer(prompt.Messages.ToList()));
        parsed.Errors.ShouldBeEmpty();
        parsed.Policy!.IsMixed.ShouldBeTrue();
        parsed.Policy.TopicLabel.ShouldBeNull();
        parsed.Rules.Select(r => (r.Match.Category, r.Action)).ShouldBe(
        [
            (MessageCategory.Promotions, PolicyAction.Delete),
            (MessageCategory.Updates, PolicyAction.Archive),
        ]);
        parsed.NewLabels.ShouldBe(["Promotions", "Updates/Example"], ignoreOrder: true);
    }

    [Fact]
    public void Fake_still_answers_analysis_prompts_as_before()
    {
        FakeAnalysisResponder.Answer([new ChatMessage(ChatRole.User, "hello")]).ShouldBe(FakeAnalysisResponder.FixedAnswer);
    }

    internal static SenderProfile Profile(CategoryMix mix, bool allowlisted = false) => new(
        PolicyScope.Sender,
        "news@example.com",
        ["Example News"],
        ["news@example.com"],
        new SenderProfileStats(mix.Primary + mix.Promotions + mix.Social + mix.Updates + mix.Forums + mix.None, 0.5, 0, 0, 1, 1,
            mix, SenderKind.Bulk, null, null, allowlisted),
        [new SenderTemplate("weekly offer #", 10, "Weekly offer 1", true, true, mix, 0, 0.5, "bulk")],
        0,
        0,
        [],
        [],
        []);
}
