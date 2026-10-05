using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Prompts;

namespace GmailOrganiser.Tests.Unit.Analysis;

public sealed class SuggestionOutputParserMailTypeTests
{
    private const string ActionLabel = "Action/Test";
    private const string DeleteLabel = "Delete/Test";
    private static readonly HashSet<string> Ids = ["m1"];
    private static readonly SuggestionParseContext Context = new(new LabelTreeIndex(["Topic", "Topic/Sub"]), [ActionLabel, DeleteLabel]);

    private static string Item(string extra, string label = "Topic/Sub", bool isNewLabel = false) =>
        $$"""[{"id":"m1","topicLabel":"{{label}}","isNewLabel":{{(isNewLabel ? "true" : "false")}},"needsAction":false,"toBeDeleted":false,"unsubscribeSuggested":false,"confidence":0.9,"reason":"Synthetic reason"{{extra}}}]""";

    private static ParsedSuggestions Parse(string raw, SuggestionParseContext? context = null) =>
        SuggestionOutputParser.Parse(raw, Ids, context: context ?? Context);

    [Theory]
    [InlineData(",\"mailType\":\"receipt\"", MailType.Receipt)]
    [InlineData(",\"mailType\":\" Action_Bill \"", MailType.ActionBill)]
    [InlineData(",\"mailType\":\"security_otp\"", MailType.SecurityOtp)]
    public void Known_mail_type_is_read(string extra, MailType expected)
    {
        var result = Parse(Item(extra));

        result.Valid.ShouldHaveSingleItem().MailType.ShouldBe(expected);
        result.Dropped.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(",\"mailType\":\"spam\"")]
    [InlineData(",\"mailType\":42")]
    [InlineData(",\"mailType\":null")]
    [InlineData("")]
    public void Unknown_or_missing_mail_type_is_null_with_a_note_and_the_suggestion_stays(string extra)
    {
        var result = Parse(Item(extra));

        result.Errors.ShouldBeEmpty();
        result.Valid.ShouldHaveSingleItem().MailType.ShouldBeNull();
        result.Dropped.ShouldHaveSingleItem().ShouldStartWith("Email 'm1': 'mailType' is missing or not one of personal, action_bill");
    }

    [Fact]
    public void Template_without_mail_types_stores_none_silently()
    {
        var result = Parse(Item(",\"mailType\":\"receipt\""), Context with { ReadMailType = false });

        result.Valid.ShouldHaveSingleItem().MailType.ShouldBeNull();
        result.Dropped.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Topic/Sub", false)]
    [InlineData("topic/sub", false)]
    [InlineData("Topic/Brand new", true)]
    public void Is_new_label_is_computed_from_the_label_tree_not_the_model(string label, bool expected)
    {
        var result = Parse(Item(",\"mailType\":\"receipt\"", label, isNewLabel: !expected));

        result.Valid.ShouldHaveSingleItem().IsNewLabel.ShouldBe(expected);
        result.NewLabelDisagreements.ShouldBe(1);
    }

    [Fact]
    public void Missing_is_new_label_is_no_error_and_no_disagreement()
    {
        var raw = Item(",\"mailType\":\"receipt\"").Replace("\"isNewLabel\":false,", "", StringComparison.Ordinal);

        var result = Parse(raw);

        result.Valid.ShouldHaveSingleItem().IsNewLabel.ShouldBeFalse();
        result.NewLabelDisagreements.ShouldBe(0);
    }

    [Theory]
    [InlineData("topic/Merchant", "Topic/Merchant")]
    [InlineData(" Other/New ", "Other/New")]
    public void Proposed_new_label_becomes_the_topic_label(string proposed, string expected)
    {
        var result = Parse(Item($",\"mailType\":\"receipt\",\"proposedNewLabel\":\"{proposed}\"", label: "Topic"));

        result.Valid.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            s => s.TopicLabel.ShouldBe(expected),
            s => s.ProposedNewLabel.ShouldBe(expected),
            s => s.IsNewLabel.ShouldBeTrue());
        result.Dropped.ShouldBeEmpty();
    }

    [Fact]
    public void Proposed_label_that_exists_is_used_but_not_new()
    {
        var result = Parse(Item(",\"mailType\":\"receipt\",\"proposedNewLabel\":\"Topic/Sub\"", label: "Topic"));

        result.Valid.ShouldHaveSingleItem().ShouldSatisfyAllConditions(s => s.TopicLabel.ShouldBe("Topic/Sub"), s => s.IsNewLabel.ShouldBeFalse());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Blank_proposed_new_label_keeps_the_topic_label(string value)
    {
        var result = Parse(Item($",\"mailType\":\"receipt\",\"proposedNewLabel\":{value}"));

        result.Valid.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            s => s.TopicLabel.ShouldBe("Topic/Sub"), s => s.ProposedNewLabel.ShouldBeNull(), s => s.IsNewLabel.ShouldBeFalse());
        result.Dropped.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("\"Action/Test\"")]
    [InlineData("\" delete/test \"")]
    [InlineData("\"Delete/Test/Sub\"")]
    [InlineData("\"INBOX\"")]
    [InlineData("\"Topic//Bad\"")]
    [InlineData("42")]
    public void Unusable_proposed_new_label_is_dropped_and_the_topic_label_stands(string value)
    {
        var result = Parse(Item($",\"mailType\":\"receipt\",\"proposedNewLabel\":{value}"));

        result.Errors.ShouldBeEmpty();
        result.Valid.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            s => s.TopicLabel.ShouldBe("Topic/Sub"), s => s.ProposedNewLabel.ShouldBeNull(), s => s.IsNewLabel.ShouldBeFalse());
        result.Dropped.ShouldHaveSingleItem().ShouldBe("Email 'm1': 'proposedNewLabel' ignored (not a usable new label).");
    }

    [Fact]
    public void Proposed_new_label_stands_in_for_a_missing_topic_label()
    {
        var raw = Item(",\"mailType\":\"receipt\",\"proposedNewLabel\":\"Topic/New\"").Replace("\"topicLabel\":\"Topic/Sub\",", "", StringComparison.Ordinal);

        Parse(raw).Valid.ShouldHaveSingleItem().TopicLabel.ShouldBe("Topic/New");
    }
}
