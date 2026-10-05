using GmailOrganiser.Policies;
using GmailOrganiser.Senders;

namespace GmailOrganiser.Tests.Unit.Policies;

/// <summary><see cref="SenderProfileBuilder.ToPromptText"/>: deterministic, compact, bounded (#355).</summary>
public sealed class SenderProfilePromptTests
{
    private static readonly DateTimeOffset Seen = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Renders_stats_as_key_value_and_one_line_per_template()
    {
        var text = SenderProfileBuilder.ToPromptText(Profile(templates: 2, otherTemplates: 3, otherMessages: 7));

        text.ShouldContain("scope: sender news@example.com\n");
        text.ShouldContain("total: 40\n");
        text.ShouldContain("unread_ratio: 0.75\n");
        text.ShouldContain("kind: bulk\n");
        text.ShouldContain("first_seen: 2025-03-01\n");
        text.ShouldContain("labels_in_use: Shopping (12)\n");
        text.ShouldContain("approved_policies_same_domain: sender billing@example.com -> Finance archive\n");
        text.ShouldContain("- 20 | \"weekly offer #\" | \"Weekly offer 0\" | list-id,unsubscribe,precedence=bulk | promotions 15, updates 5 | 0 | 0.5\n");
        text.ShouldContain("+3 other subjects (7 messages)");
        text.ShouldNotContain("body of");
    }

    [Fact]
    public void Is_deterministic()
    {
        SenderProfileBuilder.ToPromptText(Profile(templates: 10)).ShouldBe(SenderProfileBuilder.ToPromptText(Profile(templates: 10)));
    }

    [Fact]
    public void Stays_within_the_budget_without_bodies_for_worst_case_input()
    {
        var huge = new string('x', 1000);
        var profile = Profile(templates: SenderProfileBuilder.ProfileMaxTemplates, otherTemplates: 500, otherMessages: 99_999, filler: huge) with
        {
            DisplayNames = [huge, huge, huge],
            Addresses = [.. Enumerable.Range(0, 10).Select(i => $"{huge}{i}@example.com")],
            LabelsInUse = [.. Enumerable.Range(0, 5).Select(i => new LabelUse(huge + i, 1000))],
            ApprovedPolicyHints = [.. Enumerable.Range(0, 5).Select(i => new PolicyHint(PolicyScope.Sender, $"{huge}{i}@example.com", false, huge, huge, PolicyAction.Delete))],
        };

        var text = SenderProfileBuilder.ToPromptText(profile);

        text.Length.ShouldBeLessThanOrEqualTo(SenderProfileBuilder.PromptMaxChars);
        text.ShouldContain(" other subjects (");
    }

    [Fact]
    public void Folds_templates_that_do_not_fit_into_the_other_line_with_their_messages()
    {
        var longText = new string('y', 1000);
        var text = SenderProfileBuilder.ToPromptText(Profile(templates: 10, filler: longText) with
        {
            ApprovedPolicyHints = [.. Enumerable.Range(0, 5).Select(i => new PolicyHint(PolicyScope.Sender, $"{longText}{i}@example.com", false, longText, longText, PolicyAction.Keep))],
            LabelsInUse = [.. Enumerable.Range(0, 5).Select(i => new LabelUse(longText + i, 1))],
        });

        var shown = text.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal));
        shown.ShouldBeLessThan(10);
        var folded = Enumerable.Range(shown, 10 - shown).Sum(i => 20 - i);
        text.ShouldEndWith($"+{10 - shown} other subjects ({folded} messages)");
    }

    [Fact]
    public void Appends_bodies_after_the_templates()
    {
        var text = SenderProfileBuilder.ToPromptText(Profile(templates: 1) with { Bodies = [new ProfileBody("weekly offer #", "Hello from example.com")] });

        text.ShouldEndWith("body of \"weekly offer #\":\nHello from example.com");
    }

    [Fact]
    public void An_empty_subject_template_renders_as_no_subject()
    {
        var profile = Profile(templates: 0) with { Templates = [Template("", 3, null)] };

        SenderProfileBuilder.ToPromptText(profile).ShouldContain("- 3 | (no subject) | (no subject) |");
    }

    private static SenderProfile Profile(int templates, int otherTemplates = 0, int otherMessages = 0, string filler = "") => new(
        PolicyScope.Sender,
        "news@example.com",
        ["Example News"],
        ["news@example.com"],
        new SenderProfileStats(40, 0.75, 0, 1, 0.9, 0.95, new CategoryMix(0, 30, 0, 10, 0, 0), SenderKind.Bulk, Seen.AddYears(-1), Seen, false),
        [.. Enumerable.Range(0, templates).Select(i => Template($"weekly offer #{filler}{(i == 0 ? "" : " " + i)}", 20 - i, $"Weekly offer {i}{filler}"))],
        otherTemplates,
        otherMessages,
        [],
        [new LabelUse("Shopping", 12)],
        [new PolicyHint(PolicyScope.Sender, "billing@example.com", false, "Finance", null, PolicyAction.Archive)]);

    private static SenderTemplate Template(string template, int count, string? example) =>
        new(template, count, example, true, true, new CategoryMix(0, 15, 0, 5, 0, 0), 0, 0.5, "bulk");
}
