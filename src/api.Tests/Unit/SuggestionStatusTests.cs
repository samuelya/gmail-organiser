using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;

namespace GmailOrganiser.Tests.Unit;

public sealed class SuggestionStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(SuggestionStatus.Pending, AnalysisStatus.Analysed)]
    [InlineData(SuggestionStatus.Approved, AnalysisStatus.Approved)]
    [InlineData(SuggestionStatus.Rejected, AnalysisStatus.Rejected)]
    [InlineData(SuggestionStatus.Applied, AnalysisStatus.Applied)]
    public void SetStatus_mirrors_onto_the_message(SuggestionStatus status, AnalysisStatus expected)
    {
        var message = new MessageRow { Id = "msg-1", AnalysisStatus = AnalysisStatus.Analysed };
        var suggestion = new SuggestionRow { Id = Guid.NewGuid(), MessageId = "msg-1" };

        suggestion.SetStatus(status, message, Now);

        suggestion.Status.ShouldBe(status);
        message.AnalysisStatus.ShouldBe(expected);
        message.UpdatedAt.ShouldBe(Now);
        suggestion.DecidedAt.ShouldBe(status == SuggestionStatus.Pending ? null : Now);
    }

    [Fact]
    public void Applied_keeps_the_original_decision_time()
    {
        var message = new MessageRow { Id = "msg-1" };
        var suggestion = new SuggestionRow { Id = Guid.NewGuid(), MessageId = "msg-1" };

        suggestion.SetStatus(SuggestionStatus.Approved, message, Now);
        suggestion.SetStatus(SuggestionStatus.Applied, message, Now.AddMinutes(5));

        suggestion.DecidedAt.ShouldBe(Now);
        message.AnalysisStatus.ShouldBe(AnalysisStatus.Applied);
    }

    [Fact]
    public void SetStatus_rejects_another_message()
    {
        var suggestion = new SuggestionRow { Id = Guid.NewGuid(), MessageId = "msg-1" };

        Should.Throw<ArgumentException>(() => suggestion.SetStatus(SuggestionStatus.Approved, new MessageRow { Id = "msg-2" }, Now));
    }
}
