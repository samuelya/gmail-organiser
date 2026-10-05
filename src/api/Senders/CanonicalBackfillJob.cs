using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Senders;

/// <param name="After">The last relay message id re-decoded; the next chunk starts after it.</param>
/// <param name="Decoded">Relay messages whose sender decoded so far.</param>
/// <param name="Undecodable">Relay messages whose sender has no <c>_at_</c> form and stays opaque.</param>
/// <param name="Total">The relay message count taken at the start, raised to the messages seen if more appear.</param>
public sealed record CanonicalBackfillCursor(string? After, int Decoded, int Undecodable, int Total);

/// <summary>
/// "Canonical backfill" (#345): re-decodes the canonical sender of the messages stored before the decoder, reading
/// only rows whose <c>from_address</c> is at a <see cref="RelayAddressDecoder.RelayDomains"/> domain (every other row's
/// canonical sender is its address, which the migration already set). Chunks of <see cref="ChunkSize"/> in id order;
/// each chunk's writes commit with its cursor, and they are a pure function of <c>from_address</c>, so a replayed chunk
/// changes nothing. Finally the relay senders' canonical fields are recomputed. Local data only: never reads Gmail.
/// </summary>
public sealed class CanonicalBackfillJob(AppDbContext db, SenderStatsUpdater senders) : IJobHandler
{
    public const string JobType = "canonical_backfill";
    public const string Queue = JobQueues.Senders;
    public const int ChunkSize = 1000;
    public const string ProgressMessage = "Decoding relay senders";

    private static readonly string[] RelayPatterns =
        Array.ConvertAll(RelayAddressDecoder.RelayDomains, d => "%@" + SenderQuery.EscapeLike(d));

    public string Type => JobType;

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<CanonicalBackfillCursor>() ?? new CanonicalBackfillCursor(null, 0, 0, await RelayMessages().CountAsync(ct));
        while (true)
        {
            var chunk = RelayMessages();
            if (cursor.After is { } after)
            {
                chunk = chunk.Where(m => string.Compare(m.Id, after) > 0);
            }

            var rows = await chunk.OrderBy(m => m.Id).Take(ChunkSize).ToListAsync(ct);
            var decoded = 0;
            foreach (var row in rows)
            {
                decoded += MessageUpserter.SetCanonical(row) ? 1 : 0;
            }

            var seen = cursor.Decoded + cursor.Undecodable + rows.Count;
            cursor = cursor with
            {
                After = rows.Count > 0 ? rows[^1].Id : cursor.After,
                Decoded = cursor.Decoded + decoded,
                Undecodable = cursor.Undecodable + rows.Count - decoded,
                Total = Math.Max(cursor.Total, seen),
            };

            if (rows.Count < ChunkSize)
            {
                cursor = cursor with { Total = seen };
                await ctx.CompleteAsync(cursor, ProgressOf(cursor, final: true), async t =>
                {
                    await db.SaveChangesAsync(t);
                    await senders.UpdateCanonicalAsync(await RelaySenderAddressesAsync(t), t);
                }, ct);
                return;
            }

            var signal = await ctx.CheckpointAsync(cursor, ProgressOf(cursor, final: false), db.SaveChangesAsync, ct);
            db.ChangeTracker.Clear();
            if (signal != JobSignal.Continue)
            {
                return;
            }
        }
    }

    private IQueryable<MessageRow> RelayMessages() =>
        db.Messages.Where(m => RelayPatterns.Any(p => EF.Functions.Like(m.FromAddress, p)));

    private Task<List<string>> RelaySenderAddressesAsync(CancellationToken ct) =>
        db.Senders.Where(s => RelayPatterns.Any(p => EF.Functions.Like(s.Address, p))).Select(s => s.Address).ToListAsync(ct);

    /// <summary>The final message carries the counts the owner checks: how many relay senders stay opaque.</summary>
    private static JobProgress ProgressOf(CanonicalBackfillCursor cursor, bool final) => new(
        cursor.Decoded + cursor.Undecodable,
        cursor.Total,
        final ? $"Relay messages: {cursor.Decoded} decoded, {cursor.Undecodable} undecodable" : ProgressMessage);
}
