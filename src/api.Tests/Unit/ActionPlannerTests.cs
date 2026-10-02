using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit;

public sealed class ActionPlannerTests
{
    private static readonly AppSettings Settings = new() { ActionLabelName = "Synthetic Action", DeleteLabelName = "Synthetic Delete" };

    private static readonly Dictionary<string, string> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Topic/Sub"] = "L1",
        ["Synthetic Action"] = "L2",
        ["Synthetic Delete"] = "L3",
    };

    [Fact]
    public void Normal_mail_gets_the_topic_and_leaves_the_inbox()
    {
        var plan = Plan(Suggestion(), Message("INBOX", "UNREAD"));

        plan.Add.ShouldBe(["L1"]);
        plan.Remove.ShouldBe(["INBOX"]);
        plan.Note.ShouldBeNull();
    }

    [Fact]
    public void Needs_action_adds_the_action_label_and_stays_in_the_inbox()
    {
        var plan = Plan(Suggestion(needsAction: true), Message("INBOX"));

        plan.Add.ShouldBe(["L1", "L2"]);
        plan.Remove.ShouldBeEmpty();
    }

    [Fact]
    public void Deletable_mail_gets_the_delete_label_and_leaves_the_inbox()
    {
        var plan = Plan(Suggestion(toBeDeleted: true), Message("INBOX"));

        plan.Add.ShouldBe(["L1", "L3"]);
        plan.Remove.ShouldBe(["INBOX"]);
        plan.Note.ShouldBeNull();
    }

    [Theory]
    [InlineData(false, "STARRED", "starred")]
    [InlineData(false, "IMPORTANT", "important")]
    [InlineData(true, "INBOX", "allowlisted sender")]
    public void Protected_mail_never_gets_the_delete_label(bool allowlisted, string label, string reason)
    {
        var plan = Plan(Suggestion(toBeDeleted: true), Message("INBOX", label), allowlisted);

        plan.Add.ShouldBe(["L1"]);
        plan.Note.ShouldBe($"protected: {reason}");
    }

    [Fact]
    public void Protection_also_wins_when_the_topic_is_the_delete_label()
    {
        var message = Message("INBOX");
        message.HasAttachment = true;

        var plan = Plan(Suggestion(topic: "synthetic delete"), message);

        plan.Add.ShouldBeEmpty();
        plan.Remove.ShouldBe(["INBOX"]);
    }

    [Fact]
    public void Labels_already_in_place_are_dropped_and_user_flags_are_never_touched()
    {
        var plan = Plan(Suggestion(), Message("L1", "STARRED", "UNREAD"));

        plan.Add.ShouldBeEmpty();
        plan.Remove.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Topic/Sub", true)]
    [InlineData("Topic/Inbox", true)]
    [InlineData("INBOX", false)]
    [InlineData("inbox/Sub", false)]
    [InlineData("Sent Mail/Sub/Deeper", false)]
    [InlineData("Topic//Sub", false)]
    public void Label_paths_with_a_reserved_level_are_refused(string path, bool valid) =>
        LabelResolver.IsValid(path).ShouldBe(valid);

    private static ActionPlan Plan(SuggestionRow suggestion, MessageRow message, bool allowlisted = false) =>
        ActionPlanner.Plan(suggestion, message, Ids, Settings, allowlisted);

    private static SuggestionRow Suggestion(string topic = "Topic/Sub", bool needsAction = false, bool toBeDeleted = false) => new()
    {
        Id = Guid.NewGuid(),
        MessageId = "m1",
        SenderAddress = "sender@example.com",
        TopicLabel = topic,
        NeedsAction = needsAction,
        ToBeDeleted = toBeDeleted,
    };

    private static MessageRow Message(params string[] labels) => new() { Id = "m1", FromAddress = "sender@example.com", LabelIds = labels };
}
