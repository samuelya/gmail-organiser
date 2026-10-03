using System.Text.Json;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Npgsql;

namespace GmailOrganiser.Jobs;

public enum JobActionResult
{
    Ok,
    NotFound,
    Conflict,

    /// <summary>The job type's <see cref="IJobCancelHook"/> refused: the job must be resumed to finish its work.</summary>
    Refused,
}

public interface IJobService
{
    /// <summary>How many finished jobs <see cref="ListAsync"/> adds after the active ones.</summary>
    const int RecentFinishedCount = 20;

    /// <summary>
    /// Queues a job, or returns the existing one of that type and <paramref name="dedupKey"/> that is queued, running
    /// or paused; <c>Created</c> says which, also when a concurrent enqueue won the insert.
    /// </summary>
    /// <param name="dedupKey">Allows one active job per key instead of per type (see <see cref="JobRow.DedupKey"/>).</param>
    Task<(JobDto Job, bool Created)> EnqueueAsync(
        string type, string queue, object? initialCursor = null, CancellationToken ct = default, string? dedupKey = null);

    Task<JobActionResult> PauseAsync(Guid id, CancellationToken ct);

    /// <summary>Paused or failed → queued, cursor kept.</summary>
    Task<JobActionResult> ResumeAsync(Guid id, CancellationToken ct);

    /// <summary>Queued, paused or failed → cancelled unless the type's <see cref="IJobCancelHook"/> refuses; running → requested.</summary>
    Task<JobActionResult> CancelAsync(Guid id, CancellationToken ct);

    Task<JobDto?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Active jobs, plus the last <see cref="RecentFinishedCount"/> finished ones unless <paramref name="activeOnly"/>.</summary>
    Task<IReadOnlyList<JobDto>> ListAsync(bool activeOnly, CancellationToken ct);
}

internal sealed class JobService(AppDbContext db, TimeProvider time, JobNotifier notifier, IServiceProvider services) : IJobService
{
    // A transition races the runner (claim, finish); retry against the fresh status a few times.
    private const int MaxAttempts = 5;

    public async Task<(JobDto Job, bool Created)> EnqueueAsync(
        string type, string queue, object? initialCursor = null, CancellationToken ct = default, string? dedupKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);

        // The unique index on active jobs per type and key makes this atomic: a concurrent enqueue that loses the
        // insert reads the winner's row instead.
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var existing = await db.Jobs.AsNoTracking()
                .Where(j => j.Type == type && j.DedupKey == dedupKey && JobRow.Active.Contains(j.Status))
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                return (existing.ToDto(), false);
            }

            var now = time.GetUtcNow();
            var row = new JobRow
            {
                Id = Guid.CreateVersion7(now),
                Type = type,
                Queue = queue,
                DedupKey = dedupKey,
                Status = JobStatus.Queued,
                Cursor = initialCursor is null ? null : JsonSerializer.Serialize(initialCursor, initialCursor.GetType(), JobRow.Json),
                CreatedAt = now,
                QueuedAt = now,
                UpdatedAt = now,
            };
            db.Jobs.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsActiveTypeConflict(ex))
            {
                continue;
            }
            finally
            {
                db.Entry(row).State = EntityState.Detached;
            }

            await notifier.PublishAsync(db, row.Id, ct);
            return (row.ToDto(), true);
        }

        throw new InvalidOperationException($"Could not enqueue a job of type '{type}': the active job kept changing.");
    }

    public Task<JobActionResult> PauseAsync(Guid id, CancellationToken ct) => TransitionAsync(id, (status, now) => status switch
    {
        JobStatus.Queued => q => q.SetProperty(j => j.Status, JobStatus.Paused).Touch(now),
        JobStatus.Running => q => q.SetProperty(j => j.PauseRequested, true).Touch(now),
        JobStatus.Paused => NoChange,
        _ => null,
    }, ct);

    public Task<JobActionResult> ResumeAsync(Guid id, CancellationToken ct) => TransitionAsync(id, (status, now) => status switch
    {
        JobStatus.Paused or JobStatus.Failed => q => q
            .SetProperty(j => j.Status, JobStatus.Queued)
            .SetProperty(j => j.QueuedAt, now)
            .SetProperty(j => j.Error, (string?)null)
            .SetProperty(j => j.PauseRequested, false)
            .SetProperty(j => j.CancelRequested, false)
            .SetProperty(j => j.FinishedAt, (DateTimeOffset?)null)
            .Touch(now),
        // Withdraws a pending pause. If the handler already stopped for it, the runner re-queues the job.
        JobStatus.Running => q => q.SetProperty(j => j.PauseRequested, false).Touch(now),
        JobStatus.Queued => NoChange,
        _ => null,
    }, ct);

    public async Task<JobActionResult> CancelAsync(Guid id, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().Where(j => j.Id == id).Select(j => new { j.Type, j.Status, j.Cursor, j.Version })
            .SingleOrDefaultAsync(ct);
        if (job is null || job.Status == JobStatus.Running || services.GetKeyedService<IJobCancelHook>(job.Type) is not { } hook)
        {
            return await TransitionAsync(id, (status, now) => status switch
            {
                JobStatus.Queued or JobStatus.Paused or JobStatus.Failed => q => Cancel(q, now),
                JobStatus.Running => q => q.SetProperty(j => j.CancelRequested, true).Touch(now),
                _ => null,
            }, ct);
        }

        if (!JobRow.Active.Contains(job.Status) && job.Status != JobStatus.Failed)
        {
            return JobActionResult.Conflict;
        }

        if (!hook.AllowsCancel(job.Cursor))
        {
            return JobActionResult.Refused;
        }

        // The version guard makes the cursor the hook saw the one that is cancelled; a run in between means try again.
        var now = time.GetUtcNow();
        if (await db.Jobs.Where(j => j.Id == id && j.Version == job.Version).ExecuteUpdateAsync(q => Cancel(q, now), ct) == 0)
        {
            return JobActionResult.Conflict;
        }

        await notifier.PublishAsync(db, id, ct);
        await hook.CancelledAsync(job.Cursor, ct);
        return JobActionResult.Ok;
    }

    private static UpdateSettersBuilder<JobRow> Cancel(UpdateSettersBuilder<JobRow> q, DateTimeOffset now) => q
        .SetProperty(j => j.Status, JobStatus.Cancelled)
        .SetProperty(j => j.PauseRequested, false)
        .SetProperty(j => j.FinishedAt, now)
        .Touch(now);

    public async Task<JobDto?> GetAsync(Guid id, CancellationToken ct) =>
        (await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct))?.ToDto();

    public async Task<IReadOnlyList<JobDto>> ListAsync(bool activeOnly, CancellationToken ct)
    {
        var rows = await db.Jobs.AsNoTracking()
            .Where(j => JobRow.Active.Contains(j.Status))
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);
        if (!activeOnly)
        {
            rows.AddRange(await db.Jobs.AsNoTracking()
                .Where(j => JobRow.Finished.Contains(j.Status))
                .OrderByDescending(j => j.FinishedAt ?? j.UpdatedAt)
                .Take(IJobService.RecentFinishedCount)
                .ToListAsync(ct));
        }

        return rows.ConvertAll(r => r.ToDto());
    }

    private static readonly Action<UpdateSettersBuilder<JobRow>> NoChange = _ => { };

    /// <summary>
    /// Applies the update chosen for the current status, guarded by <c>WHERE status = current</c> so a
    /// concurrent claim or finish by the runner is never overwritten. <c>null</c> means not allowed.
    /// </summary>
    private async Task<JobActionResult> TransitionAsync(
        Guid id, Func<JobStatus, DateTimeOffset, Action<UpdateSettersBuilder<JobRow>>?> choose, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var status = await db.Jobs.Where(j => j.Id == id).Select(j => (JobStatus?)j.Status).SingleOrDefaultAsync(ct);
            if (status is null)
            {
                return JobActionResult.NotFound;
            }

            var update = choose(status.Value, time.GetUtcNow());
            if (update is null)
            {
                return JobActionResult.Conflict;
            }

            if (update == NoChange)
            {
                return JobActionResult.Ok;
            }

            var current = status.Value;
            int rows;
            try
            {
                rows = await db.Jobs.Where(j => j.Id == id && j.Status == current).ExecuteUpdateAsync(update, ct);
            }
            catch (Exception ex) when (IsActiveTypeConflict(ex))
            {
                // Resuming would make a second active job of this type and key.
                return JobActionResult.Conflict;
            }

            if (rows == 1)
            {
                await notifier.PublishAsync(db, id, ct);
                return JobActionResult.Ok;
            }
        }

        return JobActionResult.Conflict;
    }

    private static bool IsActiveTypeConflict(Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) is
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: JobRow.ActiveTypeIndex };
}
