using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Claude;

/// <summary>
/// Enqueues the single <see cref="ClaudeReviewJob"/> in headless mode; an active one already picks up newly queued items.
/// Other modes leave the queue for Claude Desktop.
/// </summary>
public sealed class HeadlessClaudeReviewStarter(ISettingsStore settings, IJobService jobs) : IClaudeReviewStarter
{
    public async Task StartAsync(CancellationToken ct)
    {
        if ((await settings.GetAsync(ct)).ClaudeReviewerMode == ClaudeReviewerMode.HeadlessClaudeCode)
        {
            await jobs.EnqueueAsync(ClaudeReviewJob.JobType, JobQueues.Claude, null, ct, ClaudeReviewJob.DedupKey);
        }
    }
}
