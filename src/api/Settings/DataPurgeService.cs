using GmailOrganiser.Data;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace GmailOrganiser.Settings;

/// <summary>The outcome of <see cref="DataPurgeService.PurgeAsync"/>.</summary>
public abstract record PurgeResult
{
    private PurgeResult() { }

    /// <summary>Every listed table was emptied and its model seed data re-inserted.</summary>
    public sealed record Done(IReadOnlyList<string> Tables) : PurgeResult;

    /// <summary>A job is queued, running or paused; nothing was purged.</summary>
    public sealed record JobsActive : PurgeResult;

    /// <summary>A failed apply, undo or clean-up job has a pending chunk that may be half-applied in Gmail; nothing was purged.</summary>
    public sealed record GmailChunkPending : PurgeResult;
}

/// <summary>
/// Empties every mail-derived table so the user can start over (DESIGN §7). The table list is the EF model minus
/// <see cref="KeptTables"/>, so tables added later are purged too; the settings, the Google connection and the Data
/// Protection keys (on disk) stay. Seeded rows (the <c>fetch_state</c> singleton) come back as the migration seeds them.
/// After commit, <see cref="JobsHub.DataPurgedEvent"/> tells open pages to reload.
/// </summary>
public sealed partial class DataPurgeService(
    AppDbContext db, LabelCatalog labels, IHubContext<JobsHub> hub, ILogger<DataPurgeService> logger)
{
    /// <summary>Tables a purge never touches.</summary>
    public static readonly IReadOnlySet<string> KeptTables = new HashSet<string>(StringComparer.Ordinal) { "settings", "oauth_tokens" };

    private const int MaxAttempts = 3;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    public async Task<PurgeResult> PurgeAsync(CancellationToken ct)
    {
        // Seed data lives only in the design-time model; the runtime model drops it.
        var purged = db.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .Where(t => t.GetTableName() is { } name && !KeptTables.Contains(name))
            .ToList();
        var tables = purged.Select(t => t.GetTableName()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        // Identifiers come from the model only, never from the request.
        var sql = db.GetService<ISqlGenerationHelper>();
        var qualified = string.Join(", ", purged
            .Select(t => sql.DelimitIdentifier(t.GetTableName()!, t.GetSchema()))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var refused = await PurgeOnceAsync(purged, qualified, ct);
                if (refused is not null)
                {
                    return refused;
                }

                break;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected && attempt < MaxAttempts)
            {
                // A writer took its locks in another order; Postgres aborted the purge, so nothing was changed.
                db.ChangeTracker.Clear();
                LogDeadlockRetry(attempt);
            }
        }

        labels.Invalidate();
        LogPurged(tables.Count);
        await NotifyAsync(ct);
        return new PurgeResult.Done(tables);
    }

    /// <returns>Null when purged and committed, else why nothing was purged.</returns>
    private async Task<PurgeResult?> PurgeOnceAsync(List<IEntityType> purged, string qualified, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Every purged table in one statement, before the check: no job can become active between the check and the
        // truncate, and a writer already holding one of these tables (apply inserts action_batches, then jobs) finishes
        // first instead of waiting on a lock the purge holds while the purge waits on it.
        var lockTables = $"LOCK TABLE {qualified} IN ACCESS EXCLUSIVE MODE";
        await db.Database.ExecuteSqlRawAsync(lockTables, ct);
        if (await db.Jobs.AnyAsync(j => JobRow.Active.Contains(j.Status), ct))
        {
            return new PurgeResult.JobsActive();
        }

        // A failed apply, undo or clean-up with a pending chunk may have changed Gmail; its job row and action_log are
        // the only way to resume or undo it.
        var pending = await db.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM jobs
            WHERE type = ANY({ReviewJobTypes.WritesGmail}) AND status = {JobRow.FormatStatus(JobStatus.Failed)} AND cursor ->> 'pending' IS NOT NULL
            """).AnyAsync(ct);
        if (pending)
        {
            return new PurgeResult.GmailChunkPending();
        }

        var truncate = $"TRUNCATE TABLE {qualified} RESTART IDENTITY CASCADE";
        await db.Database.ExecuteSqlRawAsync(truncate, ct);

        foreach (var type in purged)
        {
            foreach (var seed in type.GetSeedData())
            {
                var entry = db.Entry(Activator.CreateInstance(type.ClrType)!);
                entry.CurrentValues.SetValues(seed!);
                entry.State = EntityState.Added;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }

    private async Task NotifyAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SendTimeout);
        try
        {
            await hub.Clients.All.SendAsync(JobsHub.DataPurgedEvent, timeout.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogNotifyFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged local data: {TableCount} tables")]
    private partial void LogPurged(int tableCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purge attempt {Attempt} hit a deadlock; retrying")]
    private partial void LogDeadlockRetry(int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing the purge to connected clients failed")]
    private partial void LogNotifyFailed(Exception exception);
}
