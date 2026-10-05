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
        ["Types/Invoice"] = "L4",
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

    [Fact]
    public void Keep_in_inbox_keeps_the_inbox_unless_the_suggestion_was_edited_to_delete()
    {
        var keep = Suggestion();
        keep.KeepInInbox = true;
        Plan(keep, Message("INBOX")).Remove.ShouldBeEmpty();

        var deleted = Suggestion(toBeDeleted: true);
        deleted.KeepInInbox = true;
        var plan = Plan(deleted, Message("INBOX"));
        plan.Add.ShouldBe(["L1", "L3"]);
        plan.Remove.ShouldBe(["INBOX"]);
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

        var plan = Plan(Suggestion(topic: "synthetic delete", toBeDeleted: true), message);

        plan.Add.ShouldBeEmpty();
        plan.Remove.ShouldBe(["INBOX"]);
    }

    [Theory]
    [InlineData(SuggestionSource.Llm, false)]
    [InlineData(SuggestionSource.Stage0, false)]
    [InlineData(SuggestionSource.Stage0, true)]
    public void Mail_not_to_be_deleted_whose_topic_is_the_delete_label_changes_nothing(SuggestionSource source, bool edited)
    {
        // #405: unticking "to be deleted" on a Stage-0 card keeps the mail in the inbox without the delete label.
        var suggestion = Suggestion(topic: "Synthetic Delete");
        suggestion.Source = source;
        suggestion.Edited = edited;

        var plan = Plan(suggestion, Message("INBOX", "CATEGORY_UPDATES"));

        plan.Add.ShouldBeEmpty();
        plan.Remove.ShouldBeEmpty();
        plan.Note.ShouldBe(ActionPlanner.NotDeletableNote);
    }

    [Fact]
    public void The_delete_label_is_never_added_through_the_action_label_unless_deletable()
    {
        var settings = Settings with { ActionLabelName = "Synthetic Delete" };

        Plan(Suggestion(needsAction: true), Message("INBOX"), settings: settings).Add.ShouldBe(["L1"]);
    }

    [Fact]
    public void A_deletable_suggestion_whose_topic_is_the_delete_label_adds_it_once()
    {
        var plan = Plan(Suggestion(topic: "Synthetic Delete", toBeDeleted: true), Message("INBOX"));

        plan.Add.ShouldBe(["L3"]);
        plan.Remove.ShouldBe(["INBOX"]);
        plan.Note.ShouldBeNull();
    }

    [Fact]
    public void A_protected_stage0_suggestion_changes_nothing_so_the_mail_stays_in_the_inbox()
    {
        var suggestion = Suggestion(topic: "Synthetic Delete", toBeDeleted: true);
        suggestion.Source = SuggestionSource.Stage0;

        var plan = Plan(suggestion, Message("INBOX", "STARRED"));

        plan.Add.ShouldBeEmpty();
        plan.Remove.ShouldBeEmpty();
        plan.Note.ShouldBe("protected: starred");
    }

    [Fact]
    public void An_edited_stage0_card_with_a_real_topic_follows_the_normal_protected_path()
    {
        var suggestion = Suggestion();
        suggestion.Source = SuggestionSource.Stage0;
        suggestion.Edited = true;
        var message = Message("INBOX");
        message.HasAttachment = true;

        var plan = Plan(suggestion, message);

        plan.Add.ShouldBe(["L1"]);
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

    [Fact]
    public void A_rule_switched_off_lets_the_delete_label_through_but_the_allowlist_never_does()
    {
        var off = Settings with { Protection = new ProtectionSettings(Attachments: false, Starred: false, Important: false) };
        var message = Message("INBOX", "STARRED", "IMPORTANT");
        message.HasAttachment = true;

        var plan = Plan(Suggestion(toBeDeleted: true), message, settings: off);
        var allowlisted = Plan(Suggestion(toBeDeleted: true), message, allowlisted: true, settings: off);

        plan.Add.ShouldBe(["L1", "L3"]);
        plan.Note.ShouldBeNull();
        allowlisted.Add.ShouldBe(["L1"]);
        allowlisted.Note.ShouldBe("protected: allowlisted sender");
    }

    private static readonly HashSet<string> Removable = new(StringComparer.Ordinal) { "Label_7", "Label_8", "L1", "CATEGORY_UPDATES" };

    [Fact]
    public void Replaced_labels_the_message_carries_are_removed_with_the_inbox()
    {
        var plan = Plan(Suggestion(replace: ["Label_7", "Label_8"]), Message("INBOX", "Label_7"), removable: Removable);

        plan.Add.ShouldBe(["L1"]);
        plan.Remove.ShouldBe(["Label_7", "INBOX"]);
    }

    [Fact]
    public void Replaced_labels_never_remove_a_system_label_an_added_label_or_one_gmail_no_longer_has()
    {
        var plan = Plan(
            Suggestion(replace: ["L1", "CATEGORY_UPDATES", "Label_9"], needsAction: true),
            Message("INBOX", "L1", "CATEGORY_UPDATES", "Label_9"),
            removable: Removable);

        plan.Add.ShouldBe(["L2"]);
        plan.Remove.ShouldBeEmpty();
    }

    [Fact]
    public void Without_the_label_list_nothing_replaced_is_removed()
    {
        Plan(Suggestion(replace: ["Label_7"]), Message("INBOX", "Label_7")).Remove.ShouldBe(["INBOX"]);
    }

    [Fact]
    public void A_document_type_label_is_added_next_to_the_topic_label()
    {
        var plan = Plan(Suggestion(needsAction: true, type: "types/invoice"), Message("INBOX"));

        plan.Add.ShouldBe(["L1", "L4", "L2"]);
    }

    [Theory]
    [InlineData("Topic/Sub")]
    [InlineData("INBOX")]
    [InlineData("Types/")]
    public void A_document_type_label_that_is_the_topic_or_not_a_valid_path_adds_nothing_more(string type)
    {
        Plan(Suggestion(type: type), Message("INBOX")).Add.ShouldBe(["L1"]);
    }

    [Theory]
    [InlineData("synthetic action")]
    [InlineData("Synthetic Delete")]
    public void A_document_type_label_that_is_now_the_action_or_delete_label_is_skipped(string type)
    {
        ActionPlanner.AppliesDocumentType(type, Settings).ShouldBeFalse();
        Plan(Suggestion(type: type), Message("INBOX")).Add.ShouldBe(["L1"]);
    }

    private static ActionPlan Plan(
        SuggestionRow suggestion, MessageRow message, bool allowlisted = false, AppSettings? settings = null, HashSet<string>? removable = null) =>
        ActionPlanner.Plan(
            suggestion, message, Ids, settings ?? Settings,
            allowlisted ? new Allowlist(new HashSet<string> { message.FromAddress }, []) : Allowlist.Empty, removable);

    private static SuggestionRow Suggestion(
        string topic = "Topic/Sub", bool needsAction = false, bool toBeDeleted = false, string[]? replace = null, string? type = null) => new()
        {
            DocumentTypeLabel = type,
            ReplaceLabelIds = replace ?? [],
            Id = Guid.NewGuid(),
            MessageId = "m1",
            SenderAddress = "sender@example.com",
            TopicLabel = topic,
            NeedsAction = needsAction,
            ToBeDeleted = toBeDeleted,
        };

    private static MessageRow Message(params string[] labels) => new() { Id = "m1", FromAddress = "sender@example.com", LabelIds = labels };
}
