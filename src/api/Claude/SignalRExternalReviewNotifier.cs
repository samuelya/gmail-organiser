using GmailOrganiser.Jobs;
using Microsoft.AspNetCore.SignalR;

namespace GmailOrganiser.Claude;

/// <summary>
/// Sends <see cref="JobsHub.ExternalReviewChangedEvent"/> to every client at once: item changes are rare, so nothing is
/// coalesced. A send is bounded by <see cref="SendTimeout"/> and its failure is logged, never thrown.
/// </summary>
internal sealed partial class SignalRExternalReviewNotifier(
    IHubContext<JobsHub> hub, ILogger<SignalRExternalReviewNotifier> logger) : IExternalReviewNotifier
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    public async Task NotifyAsync(ExternalReviewDto item, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SendTimeout);
        try
        {
            await hub.Clients.All.SendAsync(JobsHub.ExternalReviewChangedEvent, item, timeout.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogSendFailed(logger, item.Id, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing Claude review item {ItemId} failed")]
    private static partial void LogSendFailed(ILogger logger, Guid itemId, Exception exception);
}
