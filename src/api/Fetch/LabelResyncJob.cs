using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Fetch;

/// <param name="After">The last stored id refreshed; the next chunk starts after it.</param>
/// <param name="Done">Stored rows refreshed so far.</param>
/// <param name="Total">The stored row count taken at the start, raised to <paramref name="Done"/> if more rows appear.</param>
public sealed record LabelResyncCursor(string? After, int Done, int Total);

/// <summary>
/// "Resync labels" (DESIGN §6.1): re-reads only the labels (<c>format=minimal</c>) of every stored row, deleted ones
/// included, in id order and chunks of <see cref="AppSettings.FetchChunkSize"/>, checkpointing the last id after every
/// chunk. Refreshing is idempotent, so a chunk replayed after a crash only repeats Gmail reads. It never writes Gmail and
/// leaves <c>fetch_state</c>'s history ID alone: a label change after a row's read is still replayed by the incremental fetch.
/// The account guard runs after each chunk's Gmail read and before its writes.
/// </summary>
public sealed class LabelResyncJob(
    IGmailClient gmail,
    MessageFetchPipeline pipeline,
    LocalAccountClaim accountClaim,
    ISettingsStore settings,
    AppDbContext db) : IJobHandler
{
    public const string JobType = FetchJobTypes.LabelResync;
    public const string Queue = JobQueues.Fetch;
    public const string ProgressMessage = "Resyncing labels";

    public string Type => JobType;

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<LabelResyncCursor>() ?? new LabelResyncCursor(null, 0, await db.Messages.CountAsync(ct));
        while (true)
        {
            var chunkSize = Math.Clamp(
                (await settings.GetAsync(ct)).FetchChunkSize, SettingsValidation.MinFetchChunkSize, SettingsValidation.MaxFetchChunkSize);
            var stored = db.Messages.AsNoTracking();
            if (cursor.After is { } after)
            {
                stored = stored.Where(m => string.Compare(m.Id, after) > 0);
            }

            var ids = await stored.OrderBy(m => m.Id).Select(m => m.Id).Take(chunkSize).ToListAsync(ct);
            if (ids.Count > 0 && cursor.After is null)
            {
                // The job's first write: the rows it refreshes belong to the connected account.
                await accountClaim.ClaimAsync((await gmail.GetProfileAsync(ct)).EmailAddress, ct);
            }

            await pipeline.RefreshLabelsByIdsAsync(ids, ctx.EnsureMayWriteAsync, ct);
            var done = cursor.Done + ids.Count;
            cursor = cursor with
            {
                After = ids.Count > 0 ? ids[^1] : cursor.After,
                Done = done,
                Total = Math.Max(cursor.Total, done),
            };

            if (ids.Count < chunkSize)
            {
                // Rows stored below the cursor meanwhile were skipped; the finished bar reports what was refreshed.
                cursor = cursor with { Total = cursor.Done };
                await ctx.CompleteAsync(cursor, ProgressOf(cursor), _ => Task.CompletedTask, ct);
                return;
            }

            if (await ctx.CheckpointAsync(cursor, ProgressOf(cursor), ct) != JobSignal.Continue)
            {
                return;
            }
        }
    }

    private static JobProgress ProgressOf(LabelResyncCursor cursor) => new(cursor.Done, cursor.Total, ProgressMessage);
}
