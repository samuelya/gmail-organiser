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
}

public interface IJobService
{
    /// <summary>How many finished jobs <see cref="ListAsync"/> adds after the active ones.</summary>
    const int RecentFinishedCount = 20;

    /// <summary>Queues a job, or returns the existing one of that type that is queued, running or paused.</summary>
    Task<JobDto> EnqueueAsync(string type, string queue, object? initialCursor = null, CancellationToken ct = default);

    Task<JobActionResult> PauseAsync(Guid id, CancellationToken ct);

    /// <summary>Paused or failed → queued, cursor kept.</summary>
    Task<JobActionResult> ResumeAsync(Guid id, CancellationToken ct);

    Task<JobActionResult> CancelAsync(Guid id, CancellationToken ct);

    Task<JobDto?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Active jobs, plus the last <see cref="RecentFinishedCount"/> finished ones unless <paramref name="activeOnly"/>.</summary>
    Task<IReadOnlyList<JobDto>> ListAsync(bool activeOnly, CancellationToken ct);
}

internal sealed class JobService(AppDbContext db, TimeProvider time, JobNotifier notifier) : IJobService
{
    // A transition races the runner (claim, finish); retry against the fresh status a few times.
    private const int MaxAttempts = 5;

    public async Task<JobDto> EnqueueAsync(string type, string queue, object? initialCursor = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);

        // The unique index on active jobs per type makes this atomic: a concurrent enqueue that loses the
        // insert reads the winner's row instead.
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var existing = await db.Jobs.AsNoTracking()
                .Where(j => j.Type == type && JobRow.Active.Contains(j.Status))
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                return existing.ToDto();
            }

            var now = time.GetUtcNow();
            var row = new JobRow
            {
                Id = Guid.CreateVersion7(now),
                Type = type,
                Queue = queue,
                Status = JobStatus.Queued,
                Cursor = initialCursor is null ? null : JsonSerializer.Serialize(initialCursor, initialCursor.GetType(), JobRow.Json),
                CreatedAt = now,
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
            return row.ToDto();
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

    public Task<JobActionResult> CancelAsync(Guid id, CancellationToken ct) => TransitionAsync(id, (status, now) => status switch
    {
        JobStatus.Queued or JobStatus.Paused or JobStatus.Failed => q => q
            .SetProperty(j => j.Status, JobStatus.Cancelled)
            .SetProperty(j => j.PauseRequested, false)
            .SetProperty(j => j.FinishedAt, now)
            .Touch(now),
        JobStatus.Running => q => q.SetProperty(j => j.CancelRequested, true).Touch(now),
        _ => null,
    }, ct);

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
                // Resuming would make a second active job of this type.
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
