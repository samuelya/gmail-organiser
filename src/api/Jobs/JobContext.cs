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
    private string? cursor;

    internal JobContext(Guid jobId, string? cursor, AppDbContext db, TimeProvider time, JobNotifier notifier)
    {
        JobId = jobId;
        this.cursor = cursor;
        this.db = db;
        this.time = time;
        this.notifier = notifier;
    }

    public Guid JobId { get; }

    /// <summary>The signal returned by the last checkpoint; the runner uses it to decide the end state.</summary>
    internal JobSignal LastSignal { get; private set; } = JobSignal.Continue;

    /// <summary>The cursor from the last checkpoint (or enqueue), or default when there is none.</summary>
    public T? ReadCursor<T>() => cursor is null ? default : JsonSerializer.Deserialize<T>(cursor, JobRow.Json);

    /// <summary>
    /// Persists cursor and progress in one <c>UPDATE</c> and returns whether the user asked to pause or cancel.
    /// Call only after the unit of work the cursor points past is complete.
    /// </summary>
    public async Task<JobSignal> CheckpointAsync<T>(T cursor, JobProgress progress, CancellationToken ct)
    {
        var cursorJson = JsonSerializer.Serialize(cursor, JobRow.Json);
        var progressJson = JsonSerializer.Serialize(progress, JobRow.Json);
        var now = time.GetUtcNow();

        // Raw SQL so the write and the flag read are one statement. Not composed: Postgres can't nest UPDATE.
        var flags = await db.Database.SqlQuery<int>($"""
            UPDATE jobs SET cursor = {cursorJson}::jsonb, progress = {progressJson}::jsonb, updated_at = {now}
            WHERE id = {JobId}
            RETURNING (CASE WHEN cancel_requested THEN 2 WHEN pause_requested THEN 1 ELSE 0 END) AS "Value"
            """).ToListAsync(ct);

        this.cursor = cursorJson;
        LastSignal = flags.Count == 0 ? JobSignal.Cancel : (JobSignal)flags[0];
        await notifier.PublishAsync(db, JobId, ct);
        return LastSignal;
    }

    /// <summary>
    /// Persists the final cursor and progress once all work is done. A pause or cancel requested meanwhile is
    /// ignored: the job ends completed, because it is.
    /// </summary>
    public async Task CompleteAsync<T>(T cursor, JobProgress progress, CancellationToken ct)
    {
        var cursorJson = JsonSerializer.Serialize(cursor, JobRow.Json);
        var progressJson = JsonSerializer.Serialize(progress, JobRow.Json);
        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE jobs SET cursor = {cursorJson}::jsonb, progress = {progressJson}::jsonb, updated_at = {now}
            WHERE id = {JobId}
            """, ct);

        this.cursor = cursorJson;
        LastSignal = JobSignal.Continue;
        await notifier.PublishAsync(db, JobId, ct);
    }
}
