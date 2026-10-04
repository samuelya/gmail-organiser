using GmailOrganiser.Data;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GmailOrganiser.Settings;

/// <summary>The outcome of <see cref="DataPurgeService.PurgeAsync"/>.</summary>
public abstract record PurgeResult
{
    private PurgeResult() { }

    /// <summary>Every listed table was emptied and its model seed data re-inserted.</summary>
    public sealed record Done(IReadOnlyList<string> Tables) : PurgeResult;

    /// <summary>A job is queued, running or paused; nothing was purged.</summary>
    public sealed record JobsActive : PurgeResult;
}

/// <summary>
/// Empties every mail-derived table so the user can start over (DESIGN §7). The table list is the EF model minus
/// <see cref="KeptTables"/>, so tables added later are purged too; the settings, the Google connection and the Data
/// Protection keys (on disk) stay. Seeded rows (the <c>fetch_state</c> singleton) come back as the migration seeds them.
/// </summary>
public sealed partial class DataPurgeService(AppDbContext db, LabelCatalog labels, ILogger<DataPurgeService> logger)
{
    /// <summary>Tables a purge never touches.</summary>
    public static readonly IReadOnlySet<string> KeptTables = new HashSet<string>(StringComparer.Ordinal) { "settings", "oauth_tokens" };

    public async Task<PurgeResult> PurgeAsync(CancellationToken ct)
    {
        // Seed data lives only in the design-time model; the runtime model drops it.
        var purged = db.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .Where(t => t.GetTableName() is { } name && !KeptTables.Contains(name))
            .ToList();
        var tables = purged.Select(t => t.GetTableName()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        // Identifiers come from the model only, never from the request.
        var lockJobs = $"LOCK TABLE {Quote(db.Model.FindEntityType(typeof(JobRow))!.GetTableName()!)} IN SHARE ROW EXCLUSIVE MODE";
        var truncate = $"TRUNCATE TABLE {string.Join(", ", tables.Select(Quote))} RESTART IDENTITY CASCADE";

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Blocks every concurrent insert or update on jobs (enqueue, claim, resume) until commit, so no job can
            // become active between the check and the truncate.
            await db.Database.ExecuteSqlRawAsync(lockJobs, ct);
            if (await db.Jobs.AnyAsync(j => JobRow.Active.Contains(j.Status), ct))
            {
                return new PurgeResult.JobsActive();
            }

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
        }

        labels.Invalidate();
        LogPurged(tables.Count);
        return new PurgeResult.Done(tables);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged local data: {TableCount} tables")]
    private partial void LogPurged(int tableCount);
}
