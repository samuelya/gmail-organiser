namespace GmailOrganiser.Claude;

/// <summary>Job types of the Claude feature; they run on <see cref="Jobs.JobQueues.Claude"/>.</summary>
public static class ClaudeJobTypes
{
    /// <summary>One headless Claude Code run over the queued review items.</summary>
    public const string Review = "claude_review";
}
