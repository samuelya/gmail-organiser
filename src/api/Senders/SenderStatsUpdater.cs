using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Senders;

/// <summary>
/// Keeps <c>senders</c> current for the addresses a fetch just touched: missing rows are inserted, then
/// <c>total_count</c>, <c>analysed_count</c>, <c>last_seen_at</c> and <c>display_name</c> are recomputed from
/// <c>messages</c> and <c>first_seen_at</c> moves earlier if needed, never later (ignoring <c>deleted_in_gmail</c>;
/// Gmail lists newest first, so later chunks bring older mail) in one statement, so
/// replaying a chunk never double-counts and <c>analysed_count</c> never exceeds <c>total_count</c>. <c>applied_count</c>
/// belongs to analysis and the engagement stats to <see cref="SenderStatsRebuildJob"/>.
/// The canonical fields (<see cref="RelayAddressDecoder"/>) are set in the insert; they are a pure function of the
/// address, so only the canonical backfill rewrites them (<see cref="UpdateCanonicalAsync"/>).
/// The count updates lock their rows in address order first (<see cref="LockAsync"/>), like the stats rebuild.
/// </summary>
public sealed class SenderStatsUpdater(AppDbContext db, TimeProvider time)
{
    public async Task UpdateAsync(IEnumerable<string> addresses, CancellationToken ct)
    {
        var distinct = DistinctAddresses(addresses);
        if (distinct.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        var known = await db.Senders.Where(s => distinct.Contains(s.Address)).Select(s => s.Address).ToListAsync(ct);
        var missing = distinct.Except(known, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            // DO NOTHING: the allowlist may have stubbed the same address since the read; its row (and flag) wins.
            var domains = Array.ConvertAll(missing, a => new SenderAddress(a, null).Domain);
            var canonical = Array.ConvertAll(missing, RelayAddressDecoder.Decode);
            var canonicalAddresses = Array.ConvertAll(canonical, c => c.CanonicalAddress);
            var canonicalDomains = Array.ConvertAll(canonical, c => c.CanonicalDomain);
            var relays = Array.ConvertAll(canonical, c => c.IsRelay);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO senders (address, domain, canonical_address, canonical_domain, is_relay, total_count, analysed_count, applied_count, allowlisted, updated_at)
                SELECT a, d, ca, cd, r, 0, 0, 0, FALSE, {now}
                FROM unnest({missing}, {domains}, {canonicalAddresses}, {canonicalDomains}, {relays}) AS t(a, d, ca, cd, r)
                ON CONFLICT (address) DO NOTHING
                """,
                ct);
        }

        await LockAsync(db, distinct, ct);
        await db.Senders
            .Where(s => distinct.Contains(s.Address))
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.TotalCount, s => db.Messages.Count(m => m.FromAddress == s.Address && !m.DeletedInGmail))
                .SetProperty(
                    s => s.AnalysedCount,
                    s => db.Messages.Count(m => m.FromAddress == s.Address && !m.DeletedInGmail && m.AnalysisStatus != AnalysisStatus.NotAnalysed))
                .SetProperty(
                    s => s.LastSeenAt,
                    s => db.Messages.Where(m => m.FromAddress == s.Address && !m.DeletedInGmail).Max(m => (DateTimeOffset?)m.InternalDate))
                .SetProperty(
                    s => s.FirstSeenAt,
                    s => EF.Functions.Least(
                        s.FirstSeenAt, db.Messages.Where(m => m.FromAddress == s.Address && !m.DeletedInGmail).Min(m => (DateTimeOffset?)m.InternalDate)))
                .SetProperty(
                    s => s.DisplayName,
                    s => db.Messages
                        .Where(m => m.FromAddress == s.Address && !m.DeletedInGmail && m.FromName != null && m.FromName != "")
                        .OrderByDescending(m => m.InternalDate)
                        .Select(m => m.FromName)
                        .FirstOrDefault() ?? s.DisplayName)
                .SetProperty(s => s.UpdatedAt, now), ct);
    }

    /// <summary>
    /// Recomputes <c>analysed_count</c> (messages with any suggestion status, ignoring <c>deleted_in_gmail</c>) for
    /// <paramref name="addresses"/> in one statement; analysis calls it in the transaction that changes the statuses.
    /// </summary>
    public async Task UpdateAnalysedCountsAsync(IEnumerable<string> addresses, CancellationToken ct)
    {
        var distinct = DistinctAddresses(addresses);
        if (distinct.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        await LockAsync(db, distinct, ct);
        await db.Senders
            .Where(s => distinct.Contains(s.Address))
            .ExecuteUpdateAsync(set => set
                .SetProperty(
                    s => s.AnalysedCount,
                    s => db.Messages.Count(m => m.FromAddress == s.Address && !m.DeletedInGmail && m.AnalysisStatus != AnalysisStatus.NotAnalysed))
                .SetProperty(s => s.UpdatedAt, now), ct);
    }

    /// <summary>
    /// Sets <c>canonical_address</c>, <c>canonical_domain</c> and <c>is_relay</c> of the <paramref name="addresses"/>'
    /// rows from the decoded address in one statement; rows already right are not written. The canonical backfill
    /// calls it after a decoder change.
    /// </summary>
    public async Task UpdateCanonicalAsync(IReadOnlyList<string> addresses, CancellationToken ct)
    {
        if (addresses.Count == 0)
        {
            return;
        }

        var all = addresses.ToArray();
        var canonical = Array.ConvertAll(all, RelayAddressDecoder.Decode);
        var canonicalAddresses = Array.ConvertAll(canonical, c => c.CanonicalAddress);
        var canonicalDomains = Array.ConvertAll(canonical, c => c.CanonicalDomain);
        var relays = Array.ConvertAll(canonical, c => c.IsRelay);
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE senders AS s SET canonical_address = t.ca, canonical_domain = t.cd, is_relay = t.r
            FROM unnest({all}, {canonicalAddresses}, {canonicalDomains}, {relays}) AS t(a, ca, cd, r)
            WHERE s.address = t.a AND (s.canonical_address <> t.ca OR s.canonical_domain <> t.cd OR s.is_relay <> t.r)
            """,
            ct);
    }

    /// <summary>
    /// Locks the <paramref name="addresses"/>' sender rows in address order until the caller's transaction ends (a
    /// no-op lock outside one). Every multi-row sender writer takes its locks this way, so two of them updating
    /// overlapping rows wait for each other instead of deadlocking on opposite lock orders.
    /// </summary>
    internal static Task LockAsync(AppDbContext db, IReadOnlyCollection<string> addresses, CancellationToken ct) =>
        db.Database.SqlQuery<string>(
            $"SELECT address AS \"Value\" FROM senders WHERE address = ANY({addresses.ToArray()}) ORDER BY address FOR UPDATE").ToListAsync(ct);

    private static List<string> DistinctAddresses(IEnumerable<string> addresses) =>
        addresses.Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).ToList();
}
