using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit;

public sealed class LabelNameSettingsTests
{
    public static TheoryData<string> InvalidNames => new()
    {
        "",
        "   ",
        "INBOX",
        "inbox",
        "Spam/Sub",
        new string('a', SettingsValidation.MaxLabelNameLength + 1),
        "A//B",
        "/Leading",
        "A/B/C/D/E/F",
        "Bad\u0001Name",
    };

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void Invalid_label_names_are_field_errors(string name)
    {
        var errors = SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, ActionLabelName: name, DeleteLabelName: name));

        errors.Keys.ShouldBe(["actionLabelName", "deleteLabelName"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("Synthetic/Todo")]
    [InlineData("  Synthetic Todo  ")]
    [InlineData("A/B/C/D/E")]
    public void Valid_label_names_pass(string name)
    {
        SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, ActionLabelName: name)).ShouldBeEmpty();
    }

    [Fact]
    public void Names_must_differ_case_insensitively()
    {
        var errors = SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null,
            ActionLabelName: "Synthetic Same", DeleteLabelName: " synthetic same"));

        errors.Keys.ShouldBe(["deleteLabelName"]);
        errors["deleteLabelName"].ShouldBe(["Must differ from the action label."]);
    }

    [Fact]
    public void A_name_sent_alone_is_compared_with_the_stored_other_name()
    {
        var current = new AppSettings { ActionLabelName = "Synthetic Action", DeleteLabelName = "Synthetic Delete" };

        SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, DeleteLabelName: "SYNTHETIC ACTION"), current)
            .Keys.ShouldBe(["deleteLabelName"]);
        SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, ActionLabelName: "Synthetic Delete"), current)
            .Keys.ShouldBe(["deleteLabelName"]);
        SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, ActionLabelName: "Synthetic Other"), current)
            .ShouldBeEmpty();
    }

    [Fact]
    public void An_apply_after_a_rename_uses_the_new_names()
    {
        var renamed = new AppSettings { ActionLabelName = "Synthetic Renamed Action", DeleteLabelName = "Synthetic Renamed Delete" };
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Topic"] = "L1",
            ["Synthetic Action"] = "OLD-A",
            ["Synthetic Delete"] = "OLD-D",
            ["Synthetic Renamed Action"] = "NEW-A",
            ["Synthetic Renamed Delete"] = "NEW-D",
        };
        var suggestion = new SuggestionRow
        {
            Id = Guid.NewGuid(),
            MessageId = "m1",
            SenderAddress = "sender@example.com",
            TopicLabel = "Topic",
            NeedsAction = true,
            ToBeDeleted = true,
        };
        var message = new MessageRow { Id = "m1", FromAddress = "sender@example.com", LabelIds = ["INBOX"] };

        var plan = ActionPlanner.Plan(suggestion, message, ids, renamed, Allowlist.Empty);

        plan.Add.ShouldContain("NEW-A");
        plan.Add.ShouldContain("NEW-D");
        plan.Add.ShouldNotContain("OLD-A");
        plan.Add.ShouldNotContain("OLD-D");
    }
}
