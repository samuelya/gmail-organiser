namespace GmailOrganiser.CleanUp.Unsubscribe;

/// <summary>
/// The one-click sender for <c>GMAIL_FAKE=true</c>: never touches the network. A URL that passes
/// <see cref="UnsubscribeTargetGuard.ValidateUrl"/> answers 200, anything else fails as the real sender would.
/// </summary>
public sealed class OfflineUnsubscribeSender(ILogger<OfflineUnsubscribeSender> logger) : IUnsubscribeSender
{
    public Task<UnsubscribeSendResult> SendOneClickAsync(Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        var verdict = UnsubscribeTargetGuard.ValidateUrl(url);
        if (verdict != UnsubscribeTargetVerdict.Allowed)
        {
            logger.LogWarning("Fake one-click unsubscribe to {Host} refused: {Verdict}", url.IsAbsoluteUri ? url.Host : "", verdict);
            return Task.FromResult(new UnsubscribeSendResult(false, null));
        }

        logger.LogInformation("Fake one-click unsubscribe to {Host}: no request sent", url.Host);
        return Task.FromResult(new UnsubscribeSendResult(true, 200));
    }
}
