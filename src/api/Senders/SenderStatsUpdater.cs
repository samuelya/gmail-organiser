using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Senders;

/// <summary>
/// Keeps <c>senders</c> current for the addresses a fetch just touched: missing rows are inserted, then
/// <c>total_count</c>, <c>analysed_count</c>, <c>last_seen_at</c> and <c>display_name</c> are recomputed from
/// <c>messages</c> (ignoring <c>deleted_in_gmail</c>) in one statement, so replaying a chunk never double-counts and
/// <c>analysed_count</c> never exceeds <c>total_count</c>. <c>applied_count</c> belongs to analysis and is left alone.
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
        var missing = distinct.Except(known, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            db.Senders.AddRange(missing.Select(a => new SenderRow
            {
                Address = a,
                Domain = new SenderAddress(a, null).Domain,
                UpdatedAt = now,
            }));
            await db.SaveChangesAsync(ct);
        }

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
        await db.Senders
            .Where(s => distinct.Contains(s.Address))
            .ExecuteUpdateAsync(set => set
                .SetProperty(
                    s => s.AnalysedCount,
                    s => db.Messages.Count(m => m.FromAddress == s.Address && !m.DeletedInGmail && m.AnalysisStatus != AnalysisStatus.NotAnalysed))
                .SetProperty(s => s.UpdatedAt, now), ct);
    }

    private static List<string> DistinctAddresses(IEnumerable<string> addresses) =>
        addresses.Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).ToList();
}
