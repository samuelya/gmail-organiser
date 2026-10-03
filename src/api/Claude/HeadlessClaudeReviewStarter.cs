using GmailOrganiser.Data;
using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Claude;

/// <summary>
/// Enqueues the single <see cref="ClaudeReviewJob"/> in headless mode; a queued one picks up newly queued items. A running
/// one may already be past its last look at the queue, so a follow-up job (at most one waiting, behind it on the serial
/// <see cref="JobQueues.Claude"/> queue) takes what it misses. A queued job enqueued before a failed run would complete
/// without running, so it is replaced: this send or retry is the one that restarts the queue. Other modes leave the queue
/// for Claude Desktop.
/// </summary>
public sealed class HeadlessClaudeReviewStarter(ISettingsStore settings, IJobService jobs, AppDbContext db) : IClaudeReviewStarter
{
    public async Task StartAsync(CancellationToken ct)
    {
        if ((await settings.GetAsync(ct)).ClaudeReviewerMode != ClaudeReviewerMode.HeadlessClaudeCode)
        {
            return;
        }

        var job = await EnqueueAsync(ClaudeReviewJob.DedupKey, ct);
        if (job.Status == JobRow.FormatStatus(JobStatus.Running))
        {
            await EnqueueAsync(ClaudeReviewJob.FollowUpDedupKey, ct);
        }
    }

    private async Task<JobDto> EnqueueAsync(string dedupKey, CancellationToken ct)
    {
        var (job, created) = await jobs.EnqueueAsync(ClaudeReviewJob.JobType, JobQueues.Claude, null, ct, dedupKey);
        if (created || job.Status != JobRow.FormatStatus(JobStatus.Queued)
            || !await ClaudeReviewJob.EnqueuedBeforeFailureAsync(db, job.Id, ct))
        {
            return job;
        }

        // If the runner claims it first, the cancel only requests and the enqueue returns it running.
        await jobs.CancelAsync(job.Id, ct);
        return (await jobs.EnqueueAsync(ClaudeReviewJob.JobType, JobQueues.Claude, null, ct, dedupKey)).Job;
    }
}
