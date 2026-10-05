using System.Text.Json;
using GmailOrganiser.Data;
using GmailOrganiser.Jobs;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.CleanUp;

/// <summary>Why <see cref="RetentionService.RunNowAsync"/> queued nothing.</summary>
public enum RetentionRunRefusal
{
    None,
    Disabled,
    Running,
}

/// <summary>
/// Starts and reports the <see cref="RetentionSweepJob"/> (#368). A run is due <see cref="Interval"/> after the newest
/// sweep job row was created, whatever its outcome, so a restart catches up at most once.
/// </summary>
public sealed class RetentionService(
    AppDbContext db, ISettingsStore settingsStore, LabelCatalog catalog, TransactionalGuard guard, IJobService jobs, TimeProvider time)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>One active sweep at a time.</summary>
    public const string DedupKey = "retention";

    /// <summary>Queues a sweep when retention is on and one is due; the queued job, or null.</summary>
    public async Task<JobDto?> EnqueueIfDueAsync(CancellationToken ct)
    {
        if (!(await settingsStore.GetAsync(ct)).Retention.Enabled
            || await LastAsync(ct) is { } last && last.CreatedAt + Interval > time.GetUtcNow())
        {
            return null;
        }

        var (job, created) = await jobs.EnqueueAsync(RetentionSweepJob.JobType, RetentionSweepJob.Queue, null, ct, DedupKey);
        return created ? job : null;
    }

    /// <summary>Queues a sweep now; refused while retention is off or a sweep is active.</summary>
    public async Task<(JobDto? Job, RetentionRunRefusal Refusal)> RunNowAsync(CancellationToken ct)
    {
        if (!(await settingsStore.GetAsync(ct)).Retention.Enabled)
        {
            return (null, RetentionRunRefusal.Disabled);
        }

        var (job, created) = await jobs.EnqueueAsync(RetentionSweepJob.JobType, RetentionSweepJob.Queue, null, ct, DedupKey);
        return created ? (job, RetentionRunRefusal.None) : (job, RetentionRunRefusal.Running);
    }

    /// <exception cref="Gmail.GmailNotConnectedException">The app is not connected to Gmail.</exception>
    public async Task<RetentionStatusDto> StatusAsync(CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var last = await LastAsync(ct);
        var now = time.GetUtcNow();
        var deleteLabelId = (await catalog.FindByNameAsync(settings.DeleteLabelName, ct))?.Id;
        var (eligible, _) = await RetentionSweepJob.PlanAsync(db, guard, settings, deleteLabelId, now, ct);
        var lastMarked = last?.Cursor is { } cursor ? JsonSerializer.Deserialize<RetentionCursor>(cursor, JobRow.Json)?.MessagesDone : null;
        return new RetentionStatusDto(
            settings.Retention.Enabled,
            last?.CreatedAt,
            lastMarked,
            settings.Retention.Enabled ? (last is null ? now : last.CreatedAt + Interval) : null,
            eligible.Count);
    }

    private Task<JobRow?> LastAsync(CancellationToken ct) =>
        db.Jobs.AsNoTracking().Where(j => j.Type == RetentionSweepJob.JobType).OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
}

/// <summary>
/// Every <see cref="CheckInterval"/>, queues a retention sweep if one is due (<see cref="RetentionService.EnqueueIfDueAsync"/>).
/// Stateless, so a failed pass is logged and simply retried on the next tick.
/// </summary>
public sealed partial class RetentionScheduler(IServiceScopeFactory scopes, TimeProvider time, ILogger<RetentionScheduler> logger)
    : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, time);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<RetentionService>().EnqueueIfDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    LogPassFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retention scheduling failed; retrying on the next tick")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
