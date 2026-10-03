namespace GmailOrganiser.Claude;

/// <summary>Starts reviewing the queued items after the queue changed (headless mode); a no-op otherwise.</summary>
public interface IClaudeReviewStarter
{
    Task StartAsync(CancellationToken ct);
}
