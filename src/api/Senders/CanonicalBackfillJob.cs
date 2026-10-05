using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Senders;

/// <param name="After">The last relay message id re-decoded; the next chunk starts after it.</param>
/// <param name="Decoded">Relay-domain messages whose sender decoded so far.</param>
/// <param name="Opaque">Messages at <see cref="RelayAddressDecoder.PrivateRelayDomain"/> without the <c>_at_</c> form.</param>
/// <param name="Personal">Messages from a plain <see cref="RelayAddressDecoder.ICloudDomain"/> address (not a relay).</param>
/// <param name="Total">The relay-domain message count taken at the start, raised to the messages seen if more appear.</param>
public sealed record CanonicalBackfillCursor(string? After, int Decoded, int Opaque, int Personal, int Total)
{
    public int Seen => Decoded + Opaque + Personal;
}

/// <summary>
/// "Canonical backfill" (#345): re-decodes the canonical sender of the messages stored before the decoder, reading
/// only rows whose <c>from_address</c> is at a <see cref="RelayAddressDecoder.RelayDomains"/> domain (every other row's
/// canonical sender is its address, which the migration already set). Chunks of <see cref="ChunkSize"/> in id order;
/// each chunk reads only <c>id, from_address</c> and writes one <c>UPDATE</c> that commits with its cursor, and they are a pure function of <c>from_address</c>, so a replayed chunk
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
        var cursor = ctx.ReadCursor<CanonicalBackfillCursor>() ?? new CanonicalBackfillCursor(null, 0, 0, 0, await RelayMessages().CountAsync(ct));
        while (true)
        {
            var chunk = RelayMessages().AsNoTracking();
            if (cursor.After is { } after)
            {
                chunk = chunk.Where(m => string.Compare(m.Id, after) > 0);
            }

            var rows = await chunk.OrderBy(m => m.Id).Take(ChunkSize).Select(m => new { m.Id, m.FromAddress }).ToListAsync(ct);
            var ids = rows.ConvertAll(r => r.Id).ToArray();
            var canonical = rows.ConvertAll(r => RelayAddressDecoder.Decode(r.FromAddress)).ToArray();
            var decoded = canonical.Count(c => c.IsRelay);
            var opaque = rows.Where((r, i) => !canonical[i].IsRelay && IsPrivateRelay(r.FromAddress)).Count();
            cursor = cursor with
            {
                After = rows.Count > 0 ? rows[^1].Id : cursor.After,
                Decoded = cursor.Decoded + decoded,
                Opaque = cursor.Opaque + opaque,
                Personal = cursor.Personal + rows.Count - decoded - opaque,
            };
            cursor = cursor with { Total = Math.Max(cursor.Total, cursor.Seen) };

            if (rows.Count < ChunkSize)
            {
                cursor = cursor with { Total = cursor.Seen };
                await ctx.CompleteAsync(cursor, ProgressOf(cursor, final: true), async t =>
                {
                    await UpdateMessagesAsync(ids, canonical, t);
                    await senders.UpdateCanonicalAsync(await RelaySenderAddressesAsync(t), t);
                }, ct);
                return;
            }

            var signal = await ctx.CheckpointAsync(cursor, ProgressOf(cursor, final: false), t => UpdateMessagesAsync(ids, canonical, t), ct);
            if (signal != JobSignal.Continue)
            {
                return;
            }
        }
    }

    private static bool IsPrivateRelay(string address) =>
        address.EndsWith("@" + RelayAddressDecoder.PrivateRelayDomain, StringComparison.Ordinal);

    /// <summary>Writes the chunk's canonical sender in one statement; rows already right are not written.</summary>
    private async Task UpdateMessagesAsync(string[] ids, CanonicalSender[] canonical, CancellationToken ct)
    {
        if (ids.Length == 0)
        {
            return;
        }

        var addresses = Array.ConvertAll(canonical, c => c.CanonicalAddress);
        var domains = Array.ConvertAll(canonical, c => c.CanonicalDomain);
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE messages AS m SET canonical_address = t.ca, canonical_domain = t.cd
            FROM unnest({ids}, {addresses}, {domains}) AS t(id, ca, cd)
            WHERE m.id = t.id AND (m.canonical_address <> t.ca OR m.canonical_domain <> t.cd)
            """,
            ct);
    }

    private IQueryable<MessageRow> RelayMessages() =>
        db.Messages.Where(m => RelayPatterns.Any(p => EF.Functions.Like(m.FromAddress, p)));

    private Task<List<string>> RelaySenderAddressesAsync(CancellationToken ct) =>
        db.Senders.Where(s => RelayPatterns.Any(p => EF.Functions.Like(s.Address, p))).Select(s => s.Address).ToListAsync(ct);

    /// <summary>
    /// The final message carries the counts the owner checks: how many relay senders decoded, how many relays stay
    /// opaque, and how many were plain personal iCloud addresses (never relays, so not undecodable).
    /// </summary>
    private static JobProgress ProgressOf(CanonicalBackfillCursor cursor, bool final) => new(
        cursor.Seen,
        cursor.Total,
        final
            ? $"Relay-domain messages: {cursor.Decoded} decoded, {cursor.Opaque} opaque relay, {cursor.Personal} personal iCloud"
            : ProgressMessage);
}
