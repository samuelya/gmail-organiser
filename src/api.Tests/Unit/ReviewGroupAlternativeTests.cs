using GmailOrganiser.Review;

namespace GmailOrganiser.Tests.Unit;

/// <summary>A group's alternative is copied only when every member's is; copies mixed with LLM answers are mixed (#578).</summary>
public sealed class ReviewGroupAlternativeTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SuggestionDto Member(bool copied, string topic = "Topic/One") => new(
        Guid.NewGuid(), "m1", "Order shipped", At, null, "llm", "Topic/One", false, false, false, false, 0.9, "synthetic",
        "Pending", false, false, [], [], LabelChange.Add,
        Alternative: new SuggestionAlternativeDto(
            topic, null, [], LabelChange.Add, false, false, false, 0.8, "synthetic", null, null, At, Copied: copied));

    private static SuggestionAlternativeDto Group(params SuggestionDto[] members) =>
        ReviewQuery.GroupAlternative(members, members.Length).ShouldNotBeNull();

    [Fact]
    public void None_copied_is_neither_copied_nor_mixed()
    {
        var group = Group(Member(false), Member(false), Member(false));

        (group.Copied, group.Mixed).ShouldBe((false, false));
    }

    [Fact]
    public void All_copied_is_copied_and_not_mixed()
    {
        var group = Group(Member(true), Member(true), Member(true));

        (group.Copied, group.Mixed).ShouldBe((true, false));
    }

    [Fact]
    public void One_copied_among_llm_answers_is_mixed_not_copied()
    {
        var group = Group(Member(true), Member(false), Member(false));

        (group.Copied, group.Mixed).ShouldBe((false, true));
    }

    [Fact]
    public void All_copied_with_different_outcomes_is_copied_and_mixed()
    {
        var group = Group(Member(true), Member(true, "Topic/Two"), Member(true));

        (group.Copied, group.Mixed).ShouldBe((true, true));
        group.TopicLabel.ShouldBe("Topic/One");
    }
}
