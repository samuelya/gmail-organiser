namespace GmailOrganiser.Claude;

/// <summary>Publishes a Claude review item after a committed status or resolution change.</summary>
public interface IExternalReviewNotifier
{
    /// <summary>Never throws for a delivery failure: the change is already committed.</summary>
    Task NotifyAsync(ExternalReviewDto item, CancellationToken ct);
}
