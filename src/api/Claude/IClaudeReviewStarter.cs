namespace GmailOrganiser.Claude;

/// <summary>Starts reviewing the queued items after the queue changed (headless mode); a no-op otherwise.</summary>
public interface IClaudeReviewStarter
{
    Task StartAsync(CancellationToken ct);
}

/// <summary>The default: queued items wait for Claude Desktop (or a later runner) to pick them up.</summary>
public sealed class NoClaudeReviewStarter : IClaudeReviewStarter
{
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
}
