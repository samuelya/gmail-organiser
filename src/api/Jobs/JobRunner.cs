using System.Collections.Concurrent;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
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

    /// <summary>
    /// Ends the run of every <c>running</c> job, since the process that ran it is gone. A cancel or pause
    /// requested before the restart is honoured; any other job goes back to <c>queued</c>.
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        var count = await db.Jobs.Where(j => j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => SetRequestedEnd(s, now), ct);
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
        try
        {
            await ClaimEachAsync(db, notifier, idleQueues, claimed, ct);
        }
        catch (Exception ex) when (claimed.Count > 0 && ex is not OperationCanceledException)
        {
            // Jobs claimed before the failure are already running: hand them over so they don't block their queues.
            LogPollFailed(logger, ex);
        }

        return claimed;
    }

    private async Task ClaimEachAsync(
        AppDbContext db, JobNotifier notifier, List<string> idleQueues, List<Guid> claimed, CancellationToken ct)
    {
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
                    .Touch(now), ct);
            if (rows == 1)
            {
                claimed.Add(id);
                await notifier.PublishAsync(db, id, ct);
            }
        }
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
    /// Runs a claimed job to its next end state. Host shutdown (<paramref name="ct"/> cancelled, whether the
    /// handler throws or returns early) leaves it <c>running</c> so <see cref="RecoverAsync"/> re-queues it on the next start.
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

        // A guard failure is a run failure like a handler's: recorded on the job, not left running.
        string? refused;
        try
        {
            refused = services.GetKeyedService<IJobStartGuard>(job.Queue) is { } guard ? await guard.RefuseReasonAsync(ct) : null;
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

        if (refused is not null)
        {
            LogRefused(logger);
            await FinishAsync(db, notifier, jobId, JobStatus.Failed, refused);
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

        if (context.LastSignal != JobSignal.Continue)
        {
            // Stopped at a checkpoint on request. The flags as they are now decide the end state, so a
            // pause withdrawn by a resume after that checkpoint re-queues the job instead of pausing it.
            LogStopped(logger);
            await EndRunAsync(db, notifier, jobId, s => SetRequestedEnd(s, time.GetUtcNow()));
            return;
        }

        if (ct.IsCancellationRequested)
        {
            // The handler honoured shutdown by returning early; its work is not done.
            LogInterrupted(logger);
            return;
        }

        LogFinished(logger, JobRow.FormatStatus(JobStatus.Completed));
        await FinishAsync(db, notifier, jobId, JobStatus.Completed, null);
    }

    private Task FinishAsync(AppDbContext db, JobNotifier notifier, Guid jobId, JobStatus status, string? error)
    {
        var now = time.GetUtcNow();
        return EndRunAsync(db, notifier, jobId, s => s
            .SetProperty(j => j.Status, status)
            .SetProperty(j => j.Error, error)
            .SetProperty(j => j.PauseRequested, false)
            .SetProperty(j => j.CancelRequested, false)
            .SetProperty(j => j.FinishedAt, now)
            .Touch(now));
    }

    // Not cancellable: the outcome must be recorded even while the host stops.
    private static async Task EndRunAsync(
        AppDbContext db, JobNotifier notifier, Guid jobId, Action<UpdateSettersBuilder<JobRow>> update)
    {
        await db.Jobs.Where(j => j.Id == jobId && j.Status == JobStatus.Running).ExecuteUpdateAsync(update, CancellationToken.None);
        await notifier.PublishAsync(db, jobId, CancellationToken.None);
    }

    /// <summary>End state of a run stopped by request: cancelled, else paused, else back to queued.</summary>
    private static void SetRequestedEnd(UpdateSettersBuilder<JobRow> s, DateTimeOffset now) => s
        .SetProperty(j => j.Status, j => j.CancelRequested ? JobStatus.Cancelled : j.PauseRequested ? JobStatus.Paused : JobStatus.Queued)
        .SetProperty(j => j.FinishedAt, j => j.CancelRequested ? now : (DateTimeOffset?)null)
        .SetProperty(j => j.PauseRequested, false)
        .SetProperty(j => j.CancelRequested, false)
        .Touch(now);

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-queued {Count} job(s) interrupted by the previous shutdown")]
    private static partial void LogRecovered(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job starting")]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job refused by its queue's start guard")]
    private static partial void LogRefused(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job ended as {Status}")]
    private static partial void LogFinished(ILogger logger, string status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job stopped at a checkpoint on request")]
    private static partial void LogStopped(ILogger logger);

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
