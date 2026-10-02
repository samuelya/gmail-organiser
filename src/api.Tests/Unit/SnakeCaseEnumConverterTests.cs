using GmailOrganiser.Data;
using GmailOrganiser.Fetch;

namespace GmailOrganiser.Tests.Unit;

public sealed class SnakeCaseEnumConverterTests
{
    [Theory]
    [InlineData(MailboxPhase.NotStarted, "not_started")]
    [InlineData(MailboxPhase.AllMail, "all_mail")]
    [InlineData(MailboxPhase.Completed, "completed")]
    public void Converts_both_ways(MailboxPhase value, string stored)
    {
        SnakeCaseEnumConverter<MailboxPhase>.ToDb(value).ShouldBe(stored);
        SnakeCaseEnumConverter<MailboxPhase>.FromDb(stored).ShouldBe(value);
    }

    [Fact]
    public void Unknown_value_throws()
    {
        Should.Throw<InvalidOperationException>(() => SnakeCaseEnumConverter<AnalysisStatus>.FromDb("NotAnalysed"));
    }
}
