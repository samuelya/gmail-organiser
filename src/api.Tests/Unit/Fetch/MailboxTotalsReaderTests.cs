using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailOrganiser.Tests.Unit.Fetch;

public sealed class MailboxTotalsReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MeasureAsync_reads_the_label_totals_one_after_another()
    {
        var inner = new FakeGmailClient(new FakeTokenStore(TimeProvider.System), TimeProvider.System);
        // Holds every read so a concurrent second read would start while the first is in flight and fail.
        var gmail = new CountingGmailClient(inner) { BeforeLabelTotal = (_, _) => Task.Delay(10, Ct) };
        var reader = new MailboxTotalsReader(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<MailboxTotalsReader>.Instance);
        var profile = await inner.GetProfileAsync(Ct);

        var totals = await reader.MeasureAsync(gmail, profile, Ct);

        gmail.LabelTotalCalls.ShouldBe([MailboxFetchJob.InboxLabelId, MailboxFetchJob.SpamLabelId, MailboxFetchJob.TrashLabelId]);
        totals.Inbox.ShouldBe(await inner.GetLabelMessagesTotalAsync(MailboxFetchJob.InboxLabelId, Ct));
        var spam = await inner.GetLabelMessagesTotalAsync(MailboxFetchJob.SpamLabelId, Ct);
        var trash = await inner.GetLabelMessagesTotalAsync(MailboxFetchJob.TrashLabelId, Ct);
        totals.AllMail.ShouldBe(Math.Max(0, profile.MessagesTotal - spam - trash));
    }
}
