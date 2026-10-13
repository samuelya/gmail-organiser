using GmailOrganiser.Gmail;
using Microsoft.EntityFrameworkCore.Storage;

namespace GmailOrganiser.Rules;

/// <summary>The Gmail writes behind <see cref="FilterService"/>: a filter group's creates (#557), rows and deletes.</summary>
public sealed partial class FilterService
{
    /// <summary>A new group id for <paramref name="count"/> filters; null for one, which needs no group.</summary>
    private Guid? NewGroup(int count) => count > 1 ? Guid.CreateVersion7(time.GetUtcNow()) : null;

    /// <summary>
    /// Creates <paramref name="parts"/> in Gmail, then their rows, members of <paramref name="group"/> (#557). When a part
    /// fails, the ones created before it are deleted again; one Gmail would not delete keeps its row, so it stays listed,
    /// and the first failure is the one that surfaces. The active filters <paramref name="joined"/> become members only
    /// once every part exists.
    /// </summary>
    private async Task<List<FilterRow>> CreateGroupAsync(
        IDbContextTransaction tx, IReadOnlyList<Part> parts, Guid? group, CancellationToken ct, IReadOnlyList<FilterRow>? joined = null)
    {
        var created = new List<(GmailFilter Filter, string? RestoredFrom)>();
        try
        {
            foreach (var part in parts)
            {
                // A client gone after the first part must not leave a partial group behind.
                var filter = await gmail.CreateFilterAsync(part.Criteria, part.Action, created.Count == 0 ? ct : CancellationToken.None);
                created.Add((filter, part.RestoredFrom));
            }
        }
        catch (Exception) when (created.Count > 0)
        {
            var left = new List<(GmailFilter Filter, string? RestoredFrom)>();
            foreach (var c in created)
            {
                try
                {
                    await gmail.DeleteFilterAsync(c.Filter.Id, CancellationToken.None);
                }
                catch (Exception)
                {
                    // Any failure here (transport, timeout) only means the part stays: record it, keep the first error.
                    left.Add(c);
                }
            }

            if (left.Count > 0)
            {
                await AddRowsAsync(tx, left, group);
            }

            throw;
        }

        joined?.ToList().ForEach(r => r.GroupId = group);
        return await AddRowsAsync(tx, created, group);
    }

    /// <summary>The rows of filters Gmail just created, committed even if the client has gone, so sync never mistakes them.</summary>
    private async Task<List<FilterRow>> AddRowsAsync(
        IDbContextTransaction tx, IReadOnlyList<(GmailFilter Filter, string? RestoredFrom)> created, Guid? group)
    {
        var now = time.GetUtcNow();
        var rows = created.Select(c => new FilterRow
        {
            Id = c.Filter.Id,
            Criteria = FilterRow.WriteCriteria(c.Filter.Criteria),
            Action = FilterRow.WriteAction(c.Filter.Action),
            CriteriaSummary = FilterSnapshot.Summarise(c.Filter.Criteria),
            CreatedByApp = true,
            RestoredFrom = c.RestoredFrom,
            GroupId = group,
            FirstSeenAt = now,
            LastSeenAt = now,
            UpdatedAt = now,
        }).ToList();
        db.Filters.AddRange(rows);
        await db.SaveChangesAsync(CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);
        return rows;
    }

    /// <summary>
    /// Deletes each filter in Gmail and marks its row deleted by the app, all with one time; the rows of the deletes
    /// Gmail made are committed even when a later one fails or the client has gone.
    /// </summary>
    private async Task DeleteRowsAsync(IDbContextTransaction tx, IReadOnlyList<FilterRow> rows, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        try
        {
            foreach (var row in rows)
            {
                await gmail.DeleteFilterAsync(row.Id, row == rows[0] ? ct : CancellationToken.None);
                row.DeletedAt = now;
                row.DeletedByApp = true;
                row.UpdatedAt = now;
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception) when (rows[0].DeletedAt == now)
        {
            await tx.CommitAsync(CancellationToken.None);
            throw;
        }

        await tx.CommitAsync(CancellationToken.None);
    }

    /// <summary>One Gmail filter to create, and the deleted filter it restores.</summary>
    private sealed record Part(GmailFilterCriteria Criteria, GmailFilterAction Action, string? RestoredFrom);
}
