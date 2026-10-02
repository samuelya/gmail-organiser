using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Jobs;

/// <summary>Reads a job and hands it to <see cref="IJobProgressPublisher"/>; a publisher failure never fails the job.</summary>
internal sealed partial class JobNotifier(IJobProgressPublisher publisher, ILogger<JobNotifier> logger)
{
    public async Task PublishAsync(AppDbContext db, Guid jobId, CancellationToken ct)
    {
        try
        {
            var row = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, ct);
            if (row is not null)
            {
                await publisher.JobChangedAsync(row.ToDto(), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPublishFailed(logger, jobId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing progress for job {JobId} failed")]
    private static partial void LogPublishFailed(ILogger logger, Guid jobId, Exception exception);
}
