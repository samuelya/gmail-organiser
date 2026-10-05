using GmailOrganiser.Senders;

namespace GmailOrganiser.Tests.Unit.Senders;

public sealed class SenderStatsCalculatorTests
{
    [Fact]
    public void A_reply_makes_any_sender_human() =>
        SenderStatsCalculator.Kind(new SenderCounts(20, Unread: 20, Replied: 1, BulkHeader: 20, Promotions: 20)).ShouldBe(SenderKind.Human);

    [Theory]
    [InlineData(2, 0, SenderKind.Human)]
    [InlineData(4, 1, SenderKind.Human)]
    [InlineData(4, 2, SenderKind.Unknown)] // unread ratio 0.5 is not < 0.5
    [InlineData(1, 0, SenderKind.Unknown)] // a single message is not enough
    [InlineData(0, 0, SenderKind.Unknown)]
    public void A_clean_sender_is_human_from_two_messages_with_under_half_unread(int total, int unread, SenderKind expected) =>
        SenderStatsCalculator.Kind(new SenderCounts(total, Unread: unread, Primary: total)).ShouldBe(expected);

    [Theory]
    [InlineData(nameof(SenderCounts.BulkHeader))]
    [InlineData(nameof(SenderCounts.ListUnsubscribe))]
    [InlineData(nameof(SenderCounts.Promotions))]
    [InlineData(nameof(SenderCounts.Social))]
    public void Any_bulk_signal_rules_out_the_clean_human_branch(string signal) =>
        SenderStatsCalculator.Kind(With(new SenderCounts(10, Primary: 10), signal, 1)).ShouldBe(SenderKind.Unknown);

    [Theory]
    [InlineData(nameof(SenderCounts.BulkHeader), 8, SenderKind.Bulk)]
    [InlineData(nameof(SenderCounts.BulkHeader), 7, SenderKind.Unknown)]
    [InlineData(nameof(SenderCounts.ListUnsubscribe), 8, SenderKind.Bulk)]
    [InlineData(nameof(SenderCounts.Promotions), 8, SenderKind.Bulk)]
    [InlineData(nameof(SenderCounts.Social), 8, SenderKind.Bulk)]
    [InlineData(nameof(SenderCounts.Social), 7, SenderKind.Unknown)]
    public void Bulk_needs_80_percent_headers_or_promotions_and_social(string signal, int count, SenderKind expected) =>
        SenderStatsCalculator.Kind(With(new SenderCounts(10, Unread: 10), signal, count)).ShouldBe(expected);

    [Fact]
    public void Headers_and_list_unsubscribe_add_up_towards_bulk() =>
        SenderStatsCalculator.Kind(new SenderCounts(10, Unread: 10, ListUnsubscribe: 4, BulkHeader: 4)).ShouldBe(SenderKind.Bulk);

    [Fact]
    public void Promotions_and_social_add_up_towards_bulk() =>
        SenderStatsCalculator.Kind(new SenderCounts(10, Unread: 10, Promotions: 4, Social: 4)).ShouldBe(SenderKind.Bulk);

    [Theory]
    [InlineData(10, 2, 2, SenderKind.Mixed)]
    [InlineData(10, 2, 1, SenderKind.Unknown)] // primary + updates under 20 %
    [InlineData(10, 1, 9, SenderKind.Unknown)] // promotions + social under 20 %
    [InlineData(9, 2, 7, SenderKind.Unknown)] // fewer than ten messages
    [InlineData(20, 7, 13, SenderKind.Mixed)]
    public void Mixed_needs_ten_messages_and_20_percent_on_each_side(int total, int promotions, int primary, SenderKind expected) =>
        SenderStatsCalculator.Kind(new SenderCounts(total, Unread: total, Promotions: promotions, Primary: primary)).ShouldBe(expected);

    [Fact]
    public void Updates_count_on_the_personal_side_and_social_on_the_marketing_side() =>
        SenderStatsCalculator.Kind(new SenderCounts(10, Unread: 10, Social: 2, Updates: 2)).ShouldBe(SenderKind.Mixed);

    [Fact]
    public void Bulk_wins_over_mixed() =>
        SenderStatsCalculator.Kind(new SenderCounts(10, Unread: 10, Promotions: 8, Primary: 2)).ShouldBe(SenderKind.Bulk);

    private static SenderCounts With(SenderCounts c, string signal, int count) => signal switch
    {
        nameof(SenderCounts.BulkHeader) => c with { BulkHeader = count },
        nameof(SenderCounts.ListUnsubscribe) => c with { ListUnsubscribe = count },
        nameof(SenderCounts.Promotions) => c with { Promotions = count },
        nameof(SenderCounts.Social) => c with { Social = count },
        _ => throw new ArgumentOutOfRangeException(nameof(signal)),
    };
}
