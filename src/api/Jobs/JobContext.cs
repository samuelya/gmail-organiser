using System.Text.Json;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Jobs;

public enum JobSignal
{
    Continue,
    Pause,
    Cancel,
}

/// <summary>What a running handler sees of its job: the stored cursor and the checkpoint call.</summary>
public sealed class JobContext
{
    private readonly AppDbContext db;
    private readonly TimeProvider time;
    private readonly JobNotifier notifier;
    private readonly IJobRunGuard? guard;
    private string? cursor;

    internal JobContext(Guid jobId, string? cursor, AppDbContext db, TimeProvider time, JobNotifier notifier, IJobRunGuard? guard = null)
    {
        JobId = jobId;
        this.cursor = cursor;
        this.db = db;
        this.time = time;
        this.notifier = notifier;
        this.guard = guard;
    }

    public Guid JobId { get; }

    /// <summary>The signal returned by the last checkpoint; the runner uses it to decide the end state.</summary>
    internal JobSignal LastSignal { get; private set; } = JobSignal.Continue;

    /// <summary>The cursor from the last checkpoint (or enqueue), or default when there is none.</summary>
    public T? ReadCursor<T>() => cursor is null ? default : JsonSerializer.Deserialize<T>(cursor, JobRow.Json);

    /// <summary>Throws <see cref="JobRefusedException"/> when the job type's <see cref="IJobRunGuard"/> refuses the run.</summary>
    internal async Task EnsureAllowedAsync(CancellationToken ct)
    {
        if (guard is not null && await guard.RefuseReasonAsync(ct) is { } reason)
        {
            throw new JobRefusedException(reason);
        }
    }

    /// <summary>
    /// Asks the job type's <see cref="IJobRunGuard"/> before a unit of work's writes, so a condition that changed during
    /// its reads (e.g. a reconnect to another account) stops the job before it writes, not only at the next checkpoint.
    /// Throws <see cref="JobRefusedException"/> on a refusal, which ends the run as a checkpoint refusal does.
    /// </summary>
    public Task EnsureMayWriteAsync(CancellationToken ct) => EnsureAllowedAsync(ct);

    /// <summary>
    /// Persists cursor and progress in one <c>UPDATE</c> and returns whether the user asked to pause or cancel.
    /// Call only after the unit of work the cursor points past is complete. Asks the job type's <see cref="IJobRunGuard"/>
    /// first: a refusal throws <see cref="JobRefusedException"/> and leaves the cursor at the previous checkpoint.
    /// </summary>
    public async Task<JobSignal> CheckpointAsync<T>(T cursor, JobProgress progress, CancellationToken ct)
    {
        await EnsureAllowedAsync(ct);
        var signal = await WriteCheckpointAsync(cursor, progress, ct);
        await notifier.PublishAsync(db, JobId, ct);
        return signal;
    }

    /// <summary>
    /// A checkpoint whose <paramref name="writes"/> (the unit of work's own rows) commit in the same transaction as
    /// the cursor; progress is published only after the commit. Guarded like <see cref="CheckpointAsync{T}(T, JobProgress, CancellationToken)"/>.
    /// </summary>
    public async Task<JobSignal> CheckpointAsync<T>(T cursor, JobProgress progress, Func<CancellationToken, Task> writes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writes);
        await EnsureAllowedAsync(ct);
        var (previousCursor, previousSignal) = (this.cursor, LastSignal);
        JobSignal signal;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await writes(ct);
            signal = await WriteCheckpointAsync(cursor, progress, ct);
            try
            {
                await tx.CommitAsync(ct);
            }
            catch
            {
                // Nothing was stored: the cursor in memory stays at the previous checkpoint.
                (this.cursor, LastSignal) = (previousCursor, previousSignal);
                throw;
            }
        }

        await notifier.PublishAsync(db, JobId, ct);
        return signal;
    }

    private async Task<JobSignal> WriteCheckpointAsync<T>(T cursor, JobProgress progress, CancellationToken ct)
    {
        var cursorJson = JsonSerializer.Serialize(cursor, JobRow.Json);
        var progressJson = JsonSerializer.Serialize(progress, JobRow.Json);
        var now = time.GetUtcNow();

        // Raw SQL so the write and the flag read are one statement. Not composed: Postgres can't nest UPDATE.
        var flags = await db.Database.SqlQuery<int>($"""
            UPDATE jobs SET cursor = {cursorJson}::jsonb, progress = {progressJson}::jsonb, updated_at = {now},
                version = version + 1
            WHERE id = {JobId}
            RETURNING (CASE WHEN cancel_requested THEN 2 WHEN pause_requested THEN 1 ELSE 0 END) AS "Value"
            """).ToListAsync(ct);

        this.cursor = cursorJson;
        LastSignal = flags.Count == 0 ? JobSignal.Cancel : (JobSignal)flags[0];
        return LastSignal;
    }

    /// <summary>
    /// Persists the final cursor and progress once all work is done. A pause or cancel requested meanwhile is
    /// ignored: the job ends completed, because it is. <paramref name="finalWrites"/> run in the same transaction, so
    /// the handler's own completion state is never committed without the final cursor. Guarded like a checkpoint.
    /// </summary>
    public async Task CompleteAsync<T>(T cursor, JobProgress progress, Func<CancellationToken, Task> finalWrites, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(finalWrites);
        await EnsureAllowedAsync(ct);
        var cursorJson = JsonSerializer.Serialize(cursor, JobRow.Json);
        var progressJson = JsonSerializer.Serialize(progress, JobRow.Json);
        var now = time.GetUtcNow();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await finalWrites(ct);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE jobs SET cursor = {cursorJson}::jsonb, progress = {progressJson}::jsonb, updated_at = {now},
                    version = version + 1
                WHERE id = {JobId}
                """, ct);
            await tx.CommitAsync(ct);
        }

        this.cursor = cursorJson;
        LastSignal = JobSignal.Continue;
        await notifier.PublishAsync(db, JobId, ct);
    }
}
