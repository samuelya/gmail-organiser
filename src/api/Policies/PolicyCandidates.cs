using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Policies;

/// <summary>A sender (or its mailing list) a Top senders run proposes a policy for (#357).</summary>
/// <param name="Messages">The canonical sender's live messages; the run counts them as covered.</param>
public sealed record PolicyCandidate(PolicyScope Scope, string ScopeKey, string? DisplayName, int Messages);

/// <summary>
/// Picks the senders a Top senders run walks (DESIGN §6.2): live messages grouped by canonical address, most first. A
/// sender whose mail is mostly one List-Id becomes that list. Senders whose scope key already has a proposed or approved
/// policy are skipped, and so are senders with fewer than the minimum group size (unless one sender is asked for).
/// </summary>
public static class PolicyCandidates
{
    public const int MinSenders = 1;
    public const int MaxSenders = 100;

    /// <summary>The share of a sender's mail one normalised List-Id must carry for the list to be the scope.</summary>
    public const double ListShare = 0.8;

    /// <param name="canonicalAddress">Only this sender (resolved, lower-case); null walks all senders.</param>
    public static async Task<IReadOnlyList<PolicyCandidate>> QueryAsync(
        AppDbContext db, string? canonicalAddress, int count, int minMessages, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, MinSenders);
        var live = db.Messages.AsNoTracking().Where(m => !m.DeletedInGmail && m.CanonicalAddress != "");
        if (canonicalAddress is not null)
        {
            live = live.Where(m => m.CanonicalAddress == canonicalAddress);
        }

        var perList = await live
            .GroupBy(m => new { m.CanonicalAddress, m.ListId })
            .Select(g => new { g.Key.CanonicalAddress, g.Key.ListId, Count = g.Count() })
            .ToListAsync(ct);
        // A rejection blocks the walk until the user deletes it; only a run for one sender may replace it.
        var walk = canonicalAddress is null;
        var taken = (await db.SenderPolicies.AsNoTracking()
                .Where(p => p.Status == PolicyStatus.Proposed || p.Status == PolicyStatus.Approved
                    || (walk && p.Status == PolicyStatus.Rejected))
                .Select(p => new { p.Scope, p.ScopeKey })
                .ToListAsync(ct))
            .Select(p => (p.Scope, p.ScopeKey))
            .ToHashSet();

        var min = walk ? minMessages : 1;
        var picked = perList
            .GroupBy(r => r.CanonicalAddress, StringComparer.Ordinal)
            .Select(sender =>
            {
                var total = sender.Sum(r => r.Count);
                var list = sender
                    .GroupBy(r => GroupKey.NormaliseListId(r.ListId))
                    .Where(l => l.Key is not null)
                    .Select(l => (Id: l.Key!, Count: l.Sum(r => r.Count)))
                    .OrderByDescending(l => l.Count)
                    .ThenBy(l => l.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                return list.Id is not null && list.Count >= ListShare * total
                    ? (Address: sender.Key, Scope: PolicyScope.List, Key: list.Id, Total: total)
                    : (Address: sender.Key, Scope: PolicyScope.Sender, Key: sender.Key, Total: total);
            })
            .Where(c => c.Total >= min && !taken.Contains((c.Scope, c.Key)))
            .OrderByDescending(c => c.Total)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            // Two senders of one list share its scope key: the bigger one proposes the list's policy.
            .DistinctBy(c => (c.Scope, c.Key))
            .Take(count)
            .ToList();

        var addresses = picked.Select(c => c.Address).ToArray();
        var names = (await db.Senders.AsNoTracking()
                .Where(s => addresses.Contains(s.CanonicalAddress) && s.DisplayName != null)
                .OrderByDescending(s => s.TotalCount)
                .Select(s => new { s.CanonicalAddress, s.DisplayName })
                .ToListAsync(ct))
            .DistinctBy(s => s.CanonicalAddress, StringComparer.Ordinal)
            .ToDictionary(s => s.CanonicalAddress, s => s.DisplayName, StringComparer.Ordinal);
        return [.. picked.Select(c => new PolicyCandidate(c.Scope, c.Key, names.GetValueOrDefault(c.Address), c.Total))];
    }
}
