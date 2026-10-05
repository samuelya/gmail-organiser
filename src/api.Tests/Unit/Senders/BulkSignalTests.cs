using GmailOrganiser.Fetch;
using GmailOrganiser.Senders;

namespace GmailOrganiser.Tests.Unit.Senders;

public sealed class BulkSignalTests
{
    [Theory]
    [InlineData("bulk")]
    [InlineData("list")]
    [InlineData("junk")]
    [InlineData("Bulk")]
    public void Bulk_precedence_is_bulk(string precedence) =>
        BulkSignal.Of(Message(m => m.Precedence = precedence)).ShouldBe(MessageOrigin.Bulk);

    [Fact]
    public void Other_precedence_is_not_bulk() =>
        BulkSignal.Of(Message(m => m.Precedence = "first-class")).ShouldBe(MessageOrigin.Unknown);

    [Theory]
    [InlineData("auto-generated", MessageOrigin.Bulk)]
    [InlineData("auto-replied", MessageOrigin.Bulk)]
    [InlineData("no", MessageOrigin.Unknown)]
    [InlineData("No", MessageOrigin.Unknown)]
    public void Auto_submitted_other_than_no_is_bulk(string autoSubmitted, MessageOrigin expected) =>
        BulkSignal.Of(Message(m => m.AutoSubmitted = autoSubmitted)).ShouldBe(expected);

    [Fact]
    public void List_id_is_bulk() =>
        BulkSignal.Of(Message(m => m.ListId = "news.example.com")).ShouldBe(MessageOrigin.Bulk);

    [Fact]
    public void List_unsubscribe_is_bulk() =>
        BulkSignal.Of(Message(m => m.ListUnsubscribe = "<https://example.com/u/1>")).ShouldBe(MessageOrigin.Bulk);

    [Theory]
    [InlineData(MessageCategory.Promotions, MessageOrigin.Bulk)]
    [InlineData(MessageCategory.Social, MessageOrigin.Bulk)]
    [InlineData(MessageCategory.Primary, MessageOrigin.Human)]
    [InlineData(MessageCategory.Updates, MessageOrigin.Unknown)]
    [InlineData(MessageCategory.Forums, MessageOrigin.Unknown)]
    public void Category_decides_when_no_header_does(MessageCategory category, MessageOrigin expected) =>
        BulkSignal.Of(Message(m => m.Category = category)).ShouldBe(expected);

    [Fact]
    public void A_replied_thread_without_bulk_signals_is_human() =>
        BulkSignal.Of(Message(m => m.ThreadReplied = true)).ShouldBe(MessageOrigin.Human);

    [Fact]
    public void Bulk_signals_win_over_a_replied_thread_and_primary() =>
        BulkSignal.Of(Message(m =>
        {
            m.ThreadReplied = true;
            m.Category = MessageCategory.Primary;
            m.ListId = "news.example.com";
        })).ShouldBe(MessageOrigin.Bulk);

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void No_signal_is_unknown(bool? replied) =>
        BulkSignal.Of(Message(m => m.ThreadReplied = replied)).ShouldBe(MessageOrigin.Unknown);

    private static MessageRow Message(Action<MessageRow> configure)
    {
        var row = new MessageRow { Id = "msg-1", ThreadId = "thread-1", FromAddress = "someone@example.com" };
        configure(row);
        return row;
    }
}
