using System.Globalization;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Rules;

/// <summary>
/// The local snapshot of the account's Gmail filters (<c>filters</c>): synced on demand from one <c>filters.list</c>
/// and read by every Rules feature. A sync lists Gmail inside a transaction-scoped advisory lock, so concurrent syncs
/// apply in listing order and a repeated sync converges on the same rows.
/// </summary>
public sealed class FilterSnapshot(
    AppDbContext db, IGmailClient gmail, LabelCatalog labels, LocalAccountClaim accountClaim, TimeProvider time)
{
    /// <summary>Gmail's per-account filter limit.</summary>
    public const int GmailFilterLimit = 1000;

    /// <summary>The <c>pg_advisory_xact_lock</c> key that serialises filter syncs.</summary>
    internal const long SyncLockKey = 0x6D6F_6669_6C74;

    /// <summary>
    /// Upserts every filter Gmail lists (<c>last_seen_at = now</c>, a deleted mark cleared) and marks the ones it no
    /// longer lists deleted (not by the app); already-deleted rows keep their first deletion time.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    /// <exception cref="JobRefusedException">The local data belongs to another account.</exception>
    public async Task<FilterSyncResultDto> SyncAsync(CancellationToken ct)
    {
        await accountClaim.ClaimAsync((await gmail.GetProfileAsync(ct)).EmailAddress, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({SyncLockKey})", ct);
        var listed = (await gmail.ListFiltersAsync(ct))
            .Where(f => !string.IsNullOrEmpty(f.Id))
            .DistinctBy(f => f.Id, StringComparer.Ordinal)
            .ToList();
        var now = time.GetUtcNow();
        int added = 0, removed = 0;
        var rows = await db.Filters.ToDictionaryAsync(r => r.Id, StringComparer.Ordinal, ct);
        foreach (var filter in listed)
        {
            if (!rows.TryGetValue(filter.Id, out var row))
            {
                row = new FilterRow { Id = filter.Id, FirstSeenAt = now };
                db.Filters.Add(row);
                Write(row, filter, now);
                added++;
            }
            else
            {
                if (row.DeletedAt is not null)
                {
                    row.DeletedAt = null;
                    row.DeletedByApp = false;
                    row.UpdatedAt = now;
                    added++;
                }

                if (row.ReadCriteria() != filter.Criteria || !SameAction(row.ReadAction(), filter.Action))
                {
                    Write(row, filter, now);
                }
            }

            row.LastSeenAt = now;
        }

        var listedIds = listed.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows.Values.Where(r => r.DeletedAt is null && !listedIds.Contains(r.Id)))
        {
            row.DeletedAt = now;
            row.DeletedByApp = false;
            row.UpdatedAt = now;
            removed++;
        }

        await db.SaveChangesAsync(ct);
        await db.FetchState.Where(s => s.Id == FetchStateRow.SingletonId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.FiltersSyncedAt, now).SetProperty(r => r.UpdatedAt, now), ct);
        await tx.CommitAsync(ct);
        return new FilterSyncResultDto(listed.Count, added, removed, now);
    }

    /// <summary>
    /// The snapshot sorted by criteria summary, label ids resolved to names (null for a label the mailbox lacks, and for
    /// every label while Gmail is disconnected or rate-limiting). Before the first sync the list is empty and
    /// <see cref="FilterListDto.SyncedAt"/> null.
    /// </summary>
    public async Task<FilterListDto> ListAsync(bool includeDeleted, CancellationToken ct)
    {
        var syncedAt = await db.FetchState.AsNoTracking()
            .Where(s => s.Id == FetchStateRow.SingletonId)
            .Select(s => s.FiltersSyncedAt)
            .SingleOrDefaultAsync(ct);
        var activeCount = await db.Filters.CountAsync(r => r.DeletedAt == null, ct);
        var shown = (await db.Filters.AsNoTracking().Where(r => includeDeleted || r.DeletedAt == null).ToListAsync(ct))
            .OrderBy(r => r.CriteriaSummary, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
        var names = shown.Count == 0 ? new Dictionary<string, string>(StringComparer.Ordinal) : await LabelNamesAsync(ct);
        return new FilterListDto(syncedAt, activeCount, GmailFilterLimit, [.. shown.Select(r => ToDto(r, names))]);
    }

    /// <summary>
    /// Deterministic text of <paramref name="criteria"/> for sorting, search and the MCP tool:
    /// <c>from:x to:y subject:"z" has:attachment larger:n -(negated) (query)</c>, only the parts present.
    /// </summary>
    public static string Summarise(GmailFilterCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(criteria.From))
        {
            parts.Add($"from:{criteria.From}");
        }

        if (!string.IsNullOrEmpty(criteria.To))
        {
            parts.Add($"to:{criteria.To}");
        }

        if (!string.IsNullOrEmpty(criteria.Subject))
        {
            parts.Add($"subject:\"{criteria.Subject}\"");
        }

        if (criteria.HasAttachment == true)
        {
            parts.Add("has:attachment");
        }

        if (criteria.Size is { } size)
        {
            var op = criteria.SizeComparison?.ToGmailString() ?? "size";
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{op}:{size}"));
        }

        if (!string.IsNullOrEmpty(criteria.NegatedQuery))
        {
            parts.Add($"-({criteria.NegatedQuery})");
        }

        if (!string.IsNullOrEmpty(criteria.Query))
        {
            parts.Add($"({criteria.Query})");
        }

        return string.Join(' ', parts);
    }

    public static FilterDto ToDto(FilterRow row, IReadOnlyDictionary<string, string> labelNames)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(labelNames);
        var c = row.ReadCriteria();
        var a = row.ReadAction();
        var criteria = new FilterCriteriaDto(
            c.From, c.To, c.Subject, c.Query, c.NegatedQuery, c.HasAttachment, c.ExcludeChats, c.Size,
            c.SizeComparison?.ToGmailString());
        var action = new FilterActionDto(
            [.. a.AddLabelIds.Select(id => new LabelRefDto(id, labelNames.GetValueOrDefault(id)))],
            a.RemoveLabelIds,
            a.RemoveLabelIds.Contains("INBOX", StringComparer.Ordinal),
            a.RemoveLabelIds.Contains("UNREAD", StringComparer.Ordinal),
            !string.IsNullOrEmpty(a.Forward));
        return new FilterDto(
            row.Id, criteria, row.CriteriaSummary, action, row.CreatedByApp, row.FirstSeenAt, row.DeletedAt, row.DeletedByApp,
            row.RestoredFrom);
    }

    /// <summary>Label names by id; empty when Gmail cannot be reached.</summary>
    internal async Task<Dictionary<string, string>> LabelNamesAsync(CancellationToken ct)
    {
        try
        {
            return (await labels.GetAsync(ct)).ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is GmailNotConnectedException or GmailRateLimitedException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void Write(FilterRow row, GmailFilter filter, DateTimeOffset now)
    {
        row.Criteria = FilterRow.WriteCriteria(filter.Criteria);
        row.Action = FilterRow.WriteAction(filter.Action);
        row.CriteriaSummary = Summarise(filter.Criteria);
        row.UpdatedAt = now;
    }

    private static bool SameAction(GmailFilterAction a, GmailFilterAction b) =>
        a.AddLabelIds.SequenceEqual(b.AddLabelIds, StringComparer.Ordinal)
        && a.RemoveLabelIds.SequenceEqual(b.RemoveLabelIds, StringComparer.Ordinal)
        && string.Equals(a.Forward, b.Forward, StringComparison.Ordinal);
}
