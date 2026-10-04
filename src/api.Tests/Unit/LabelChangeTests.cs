using GmailOrganiser.Review;

namespace GmailOrganiser.Tests.Unit;

public sealed class LabelChangeTests
{
    [Theory]
    [InlineData("Topic/Sub", "", "", LabelChange.None)]
    [InlineData("Topic/Sub", "", "topic/sub|Other", LabelChange.Keep)]
    [InlineData("Topic/Sub", "", "Other", LabelChange.Add)]
    [InlineData("Topic/Bills", "Bills", "Bills", LabelChange.Move)]
    [InlineData("Topic/bills", "Old/Bills", "Old/Bills|Other", LabelChange.Move)]
    [InlineData("Topic/Sub", "Other", "Other", LabelChange.Relabel)]
    [InlineData("Topic/Bills", "Bills|Other", "Bills|Other", LabelChange.Relabel)]
    public void Change_follows_the_topic_the_replaced_and_the_current_labels(string topic, string replace, string current, LabelChange expected) =>
        LabelChanges.For(topic, Split(replace), Split(current)).ShouldBe(expected);

    [Fact]
    public void Change_is_snake_case_on_the_wire() =>
        System.Text.Json.JsonSerializer.Serialize(LabelChange.Relabel).ShouldBe("\"relabel\"");

    private static string[] Split(string value) => value.Length == 0 ? [] : value.Split('|');
}
