using System.Collections.Concurrent;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Jobs;

/// <summary>
/// Runs queued jobs: at start-up re-queues jobs left <c>running</c> by a dead process, then every
/// <see cref="JobsOptions.PollInterval"/> claims the oldest queued job of each idle queue and runs it in
/// its own DI scope. Handler failures are recorded on the job; nothing here stops the host.
/// </summary>
public sealed partial class JobRunner(
    IServiceScopeFactory scopeFactory,
    IOptions<JobsOptions> options,
    TimeProvider time,
    ILogger<JobRunner> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Task> inFlight = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var recovered = false;
            using var timer = new PeriodicTimer(options.Value.PollInterval, time);
            do
            {
                try
                {
                    // Until recovery succeeds no job is claimed: a stale running row would block its queue.
                    if (!recovered)
                    {
                        await RecoverAsync(stoppingToken);
                        recovered = true;
                    }

                    foreach (var id in await ClaimAsync(stoppingToken))
                    {
                        var task = Task.Run(() => RunSafelyAsync(id, stoppingToken), CancellationToken.None);
                        inFlight[id] = task;
                        _ = task.ContinueWith(_ => inFlight.TryRemove(id, out var _), TaskScheduler.Default);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogPollFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
        catch (Exception ex)
        {
            LogRunnerStopped(logger, ex);
        }

        await Task.WhenAll(inFlight.Values);
    }

    /// <summary>Moves every <c>running</c> job back to <c>queued</c>: the process that ran it is gone.</summary>
    public async Task<int> RecoverAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        var count = await db.Jobs.Where(j => j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued).SetProperty(j => j.UpdatedAt, now), ct);
        if (count > 0)
        {
            LogRecovered(logger, count);
        }

        return count;
    }

    /// <summary>
    /// For each queue with no running job, claims its oldest queued job. The claim is one guarded
    /// <c>UPDATE ... WHERE status = 'queued'</c>, so a job is claimed at most once even if runners race.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ClaimAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notifier = scope.ServiceProvider.GetRequiredService<JobNotifier>();

        var idleQueues = await db.Jobs
            .Where(j => j.Status == JobStatus.Queued
                && !db.Jobs.Any(r => r.Queue == j.Queue && r.Status == JobStatus.Running))
            .Select(j => j.Queue)
            .Distinct()
            .ToListAsync(ct);

        var claimed = new List<Guid>();
        foreach (var queue in idleQueues)
        {
            var id = await db.Jobs
                .Where(j => j.Queue == queue && j.Status == JobStatus.Queued)
                .OrderBy(j => j.CreatedAt)
                .Select(j => j.Id)
                .FirstOrDefaultAsync(ct);
            var now = time.GetUtcNow();
            var rows = await db.Jobs
                .Where(j => j.Id == id && j.Status == JobStatus.Queued
                    && !db.Jobs.Any(r => r.Queue == j.Queue && r.Status == JobStatus.Running))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.Running)
                    .SetProperty(j => j.PauseRequested, false)
                    .SetProperty(j => j.CancelRequested, false)
                    .SetProperty(j => j.StartedAt, j => j.StartedAt ?? now)
                    .SetProperty(j => j.UpdatedAt, now), ct);
            if (rows == 1)
            {
                claimed.Add(id);
                await notifier.PublishAsync(db, id, ct);
            }
        }

        return claimed;
    }

    private async Task RunSafelyAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            await RunAsync(jobId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // A database failure while loading or recording the job; it stays running until the next start.
            LogRunFailed(logger, jobId, ex);
        }
    }

    /// <summary>
    /// Runs a claimed job to its next end state. Host shutdown (<paramref name="ct"/> cancelled) leaves
    /// it <c>running</c> so <see cref="RecoverAsync"/> re-queues it on the next start.
    /// </summary>
    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var notifier = services.GetRequiredService<JobNotifier>();

        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId && j.Status == JobStatus.Running, ct);
        if (job is null)
        {
            return;
        }

        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = job.Id, ["JobType"] = job.Type });
        var handler = services.GetKeyedService<IJobHandler>(job.Type);
        if (handler is null)
        {
            LogUnknownType(logger, job.Type);
            await FinishAsync(db, notifier, jobId, JobStatus.Failed, $"No handler is registered for job type '{job.Type}'.");
            return;
        }

        var context = new JobContext(job.Id, job.Cursor, db, time, notifier);
        try
        {
            LogStarting(logger);
            await handler.RunAsync(context, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogInterrupted(logger);
            return;
        }
        catch (Exception ex)
        {
            LogHandlerFailed(logger, ex);
            await FinishAsync(db, notifier, jobId, JobStatus.Failed, ex.Message);
            return;
        }

        var status = context.LastSignal switch
        {
            JobSignal.Pause => JobStatus.Paused,
            JobSignal.Cancel => JobStatus.Cancelled,
            _ => JobStatus.Completed,
        };
        LogFinished(logger, JobRow.FormatStatus(status));
        await FinishAsync(db, notifier, jobId, status, null);
    }

    // Not cancellable: the outcome must be recorded even while the host stops.
    private async Task FinishAsync(AppDbContext db, JobNotifier notifier, Guid jobId, JobStatus status, string? error)
    {
        var now = time.GetUtcNow();
        DateTimeOffset? finishedAt = status == JobStatus.Paused ? null : now;
        await db.Jobs.Where(j => j.Id == jobId && j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.PauseRequested, false)
                .SetProperty(j => j.CancelRequested, false)
                .SetProperty(j => j.FinishedAt, finishedAt)
                .SetProperty(j => j.UpdatedAt, now), CancellationToken.None);
        await notifier.PublishAsync(db, jobId, CancellationToken.None);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-queued {Count} job(s) interrupted by the previous shutdown")]
    private static partial void LogRecovered(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job starting")]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job ended as {Status}")]
    private static partial void LogFinished(ILogger logger, string status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job interrupted by shutdown; it resumes from its cursor on the next start")]
    private static partial void LogInterrupted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job failed")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "No handler is registered for job type {JobType}")]
    private static partial void LogUnknownType(ILogger logger, string jobType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording the outcome of job {JobId} failed")]
    private static partial void LogRunFailed(ILogger logger, Guid jobId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Polling for jobs failed; retrying on the next tick")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Job runner stopped unexpectedly")]
    private static partial void LogRunnerStopped(ILogger logger, Exception exception);
}
