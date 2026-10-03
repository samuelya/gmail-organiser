using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Claude;

/// <summary>
/// Enqueues the single <see cref="ClaudeReviewJob"/> in headless mode; a queued one picks up newly queued items. A running
/// one may already be past its last look at the queue, so a follow-up job (at most one waiting, behind it on the serial
/// <see cref="JobQueues.Claude"/> queue) takes what it misses. Other modes leave the queue for Claude Desktop.
/// </summary>
public sealed class HeadlessClaudeReviewStarter(ISettingsStore settings, IJobService jobs) : IClaudeReviewStarter
{
    public async Task StartAsync(CancellationToken ct)
    {
        if ((await settings.GetAsync(ct)).ClaudeReviewerMode != ClaudeReviewerMode.HeadlessClaudeCode)
        {
            return;
        }

        var (job, _) = await jobs.EnqueueAsync(ClaudeReviewJob.JobType, JobQueues.Claude, null, ct, ClaudeReviewJob.DedupKey);
        if (job.Status == JobRow.FormatStatus(JobStatus.Running))
        {
            await jobs.EnqueueAsync(ClaudeReviewJob.JobType, JobQueues.Claude, null, ct, ClaudeReviewJob.FollowUpDedupKey);
        }
    }
}
