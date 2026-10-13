using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Review;
using Google;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GmailOrganiser.Rules;

public enum FilterOutcome
{
    Ok,
    NotFound,

    /// <summary>The request conflicts with the snapshot or the mailbox; <see cref="FilterResult.Detail"/> says how.</summary>
    Conflict,
}

public sealed record FilterResult(FilterOutcome Outcome, FilterDto? Filter = null, string? Title = null, string? Detail = null)
{
    public static FilterResult Conflict(string title, string detail) => new(FilterOutcome.Conflict, null, title, detail);
}

/// <summary>
/// Previews, creates, deletes and restores Gmail filters (DESIGN §6.5); besides <see cref="FilterSnapshot.SyncAsync"/>
/// the only writer of <c>filters</c> rows. Every mutation holds the sync's advisory lock around the limit check, the
/// Gmail call and the row write, so a concurrent sync neither double-inserts nor marks the new filter deleted.
/// </summary>
public sealed class FilterService(
    AppDbContext db, IGmailClient gmail, FilterSnapshot snapshot, LabelCatalog catalog, LabelResolver resolver, TimeProvider time)
{
    /// <summary>Above this relative difference between the local count and Gmail's estimate, the preview says why they can differ.</summary>
    private const double EstimateTolerance = 0.10;

    /// <summary>The criteria's query, local count and Gmail estimate; Gmail failures become warnings, never errors.</summary>
    public async Task<FilterPreviewDto> PreviewAsync(FilterSpec spec, CancellationToken ct)
    {
        var warnings = new List<string>();
        var query = FilterCriteriaMapping.ToQuery(spec.Criteria);
        int? local = null;
        if (FilterCriteriaMapping.LocalFilter(spec.Criteria, out var reason) is { } filter)
        {
            local = await filter(db.Messages.AsNoTracking().Where(m => !m.DeletedInGmail)).CountAsync(ct);
        }
        else
        {
            warnings.Add($"No local count: {reason}.");
        }

        long? estimate = null;
        try
        {
            estimate = (await gmail.ListMessageIdsAsync(new MessageListQuery(query, null, null, 1), ct)).ResultSizeEstimate;
        }
        catch (Exception ex) when (GmailWarning(ex) is { } warning)
        {
            warnings.Add($"No Gmail estimate: {warning}.");
        }

        IReadOnlyList<GmailLabel>? labels = null;
        try
        {
            labels = await catalog.GetAsync(ct);
        }
        catch (Exception ex) when (GmailWarning(ex) is { } warning)
        {
            warnings.Add($"Missing labels could not be checked: {warning}.");
        }

        if (local is { } l && estimate is { } e && Math.Abs(l - e) > EstimateTolerance * Math.Max(l, e))
        {
            warnings.Add("The local count excludes Spam, Trash and mail not fetched yet, and Gmail's estimate is approximate.");
        }

        var creates = labels is null ? [] : MissingLabels(labels, spec.AddLabelNames);
        var action = new FilterActionDto(
            [.. spec.AddLabelNames.Select(n => new LabelRefDto(
                labels is null ? "" : GmailLabel.FindByName(labels, n)?.Id ?? "", n))],
            spec.RemoveLabelIds, spec.SkipInbox, spec.MarkRead, false);
        return new FilterPreviewDto(
            ToCriteriaDto(spec.Criteria), FilterSnapshot.Summarise(spec.Criteria), query, local, estimate, action, creates, warnings);
    }

    /// <summary>
    /// Under the lock: the limit check, the labels the action names (each recorded in one
    /// <see cref="ActionKind.FilterLabels"/> batch right after its create), then one Gmail filter per user label (#557)
    /// and their rows (<c>created_by_app</c>, one group), returned as one filter.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    /// <exception cref="GoogleApiException">Gmail refused the label or the filter (e.g. the filter already exists).</exception>
    /// <exception cref="Jobs.JobRefusedException">The first sync found the local data belongs to another account.</exception>
    public async Task<FilterResult> CreateAsync(FilterSpec spec, CancellationToken ct)
    {
        await EnsureSyncedAsync(ct);
        await using var tx = await LockAsync(ct);
        var adding = Math.Max(1, spec.AddLabelNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        if (await LimitReachedAsync(ct, adding: adding) is { } full)
        {
            return full;
        }

        var batch = new ActionBatchRow
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            Kind = ActionKind.FilterLabels,
            Description = $"Labels created for a filter ({FilterSnapshot.Summarise(spec.Criteria)})",
            CreatedAt = time.GetUtcNow(),
        };
        try
        {
            var ids = await resolver.EnsureAsync(spec.AddLabelNames, (label, _) => RecordCreatedAsync(batch, label), ct);
            var action = new GmailFilterAction([.. spec.AddLabelNames.Select(n => ids[n])], spec.RemoveLabelIds);
            var names = spec.AddLabelNames.ToDictionary(n => ids[n], n => n, StringComparer.Ordinal);
            var rows = await CreateGroupAsync(tx, [.. action.SplitPerUserLabel().Select(a => new Part(spec.Criteria, a, null))], ct);
            return new FilterResult(FilterOutcome.Ok, FilterSnapshot.ToDto(rows, names));
        }
        catch (LabelLimitException ex)
        {
            await KeepCreatedLabelsAsync(tx, batch);
            return FilterResult.Conflict("Label limit reached", ex.Message);
        }
        catch (Exception) when (batch.CreatedLabelIds.Length > 0)
        {
            // The labels exist in Gmail whatever failed after them, so their record outlives the failed request.
            await KeepCreatedLabelsAsync(tx, batch);
            throw;
        }
    }

    /// <summary>Deletes the filter in Gmail and marks its row deleted by the app; a group's other members (#557) stay.</summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    /// <exception cref="GoogleApiException">Gmail refused the delete.</exception>
    public async Task<FilterResult> DeleteAsync(string id, CancellationToken ct)
    {
        await using var tx = await LockAsync(ct);
        var row = await db.Filters.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
        {
            return new FilterResult(FilterOutcome.NotFound);
        }

        if (row.DeletedAt is not null)
        {
            return FilterResult.Conflict("Filter already deleted", $"Filter '{id}' was deleted at {row.DeletedAt:O}.");
        }

        await DeleteRowsAsync(tx, [row], ct);
        return new FilterResult(FilterOutcome.Ok);
    }

    /// <summary>
    /// Deletes filter <paramref name="id"/> with every active member of its group (#557), as the user sees one filter;
    /// they share a deletion time, so a restore brings them back together.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    /// <exception cref="GoogleApiException">Gmail refused a delete; the members deleted before it stay deleted.</exception>
    public async Task<FilterResult> DeleteGroupAsync(string id, CancellationToken ct)
    {
        await using var tx = await LockAsync(ct);
        var row = await db.Filters.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
        {
            return new FilterResult(FilterOutcome.NotFound);
        }

        if (row.DeletedAt is not null)
        {
            return FilterResult.Conflict("Filter already deleted", $"Filter '{id}' was deleted at {row.DeletedAt:O}.");
        }

        var members = row.GroupId is { } group
            ? await db.Filters.Where(r => r.GroupId == group && r.DeletedAt == null).OrderBy(r => r.Id).ToListAsync(ct)
            : [row];
        await DeleteRowsAsync(tx, members, ct);
        return new FilterResult(FilterOutcome.Ok);
    }

    /// <summary>
    /// Re-creates a deleted filter, with the group members (#557) deleted with it, from their stored criteria and actions
    /// as new Gmail filters (new ids, new rows with <c>restored_from</c>, one new group); the deleted rows stay as they
    /// are. Every check runs under the lock, so two restores of one row create one filter.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    /// <exception cref="GoogleApiException">Gmail refused the filter (e.g. the user re-created it by hand).</exception>
    public async Task<FilterResult> RestoreAsync(string id, CancellationToken ct)
    {
        await using var tx = await LockAsync(ct);
        var row = await db.Filters.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.DeletedAt != null, ct);
        if (row is null)
        {
            return new FilterResult(FilterOutcome.NotFound);
        }

        var members = row.GroupId is { } group
            ? await db.Filters.AsNoTracking()
                .Where(r => r.GroupId == group && r.DeletedAt == row.DeletedAt)
                .OrderBy(r => r.FirstSeenAt).ThenBy(r => r.Id)
                .ToListAsync(ct)
            : [row];
        var ids = members.Select(m => m.Id).ToList();
        if (await db.Filters.AnyAsync(r => r.RestoredFrom != null && ids.Contains(r.RestoredFrom) && r.DeletedAt == null, ct))
        {
            return FilterResult.Conflict("Filter already restored", $"Filter '{id}' was already restored as an active filter.");
        }

        return await CreateCheckedAsync(tx, [.. members.Select(m => new Part(m.ReadCriteria(), m.ReadAction(), m.Id))], ct);
    }

    /// <summary>
    /// Creates a filter from label ids, as a filter review fix (#213) does, with a restore's checks: no forwarding, a
    /// criterion and an action, Gmail's limit, and every label still existing. The limit allows for the
    /// <paramref name="deletesAfter"/> filters the fix deletes once this one exists. Several user labels make a group
    /// (#557).
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    /// <exception cref="GoogleApiException">Gmail refused the filter.</exception>
    public async Task<FilterResult> CreateFromIdsAsync(
        GmailFilterCriteria criteria, GmailFilterAction action, int deletesAfter, CancellationToken ct)
    {
        await using var tx = await LockAsync(ct);
        return await CreateCheckedAsync(tx, [new Part(criteria, action, null)], ct, deletesAfter);
    }

    public static FilterCriteriaDto ToCriteriaDto(GmailFilterCriteria c) => new(
        c.From, c.To, c.Subject, c.Query, c.NegatedQuery, c.HasAttachment, c.ExcludeChats, c.Size, c.SizeComparison?.ToGmailString());

    private async Task<FilterResult> CreateCheckedAsync(
        IDbContextTransaction tx, IReadOnlyList<Part> items, CancellationToken ct, int deletesAfter = 0)
    {
        if (items.Any(i => !string.IsNullOrEmpty(i.Action.Forward)))
        {
            return FilterResult.Conflict("Filter forwards mail", "The app never creates a forwarding filter.");
        }

        if (items.Any(i => !i.Criteria.MatchesMail || i.Action.IsEmpty))
        {
            return FilterResult.Conflict("Filter cannot be created", "The filter has no criterion or no action.");
        }

        var parts = items.SelectMany(i => i.Action.SplitPerUserLabel().Select(a => i with { Action = a })).ToList();
        if (await LimitReachedAsync(ct, deletesAfter, parts.Count) is { } full)
        {
            return full;
        }

        var labels = await catalog.GetAsync(ct);
        if (parts.SelectMany(p => p.Action.AddLabelIds.Concat(p.Action.RemoveLabelIds)).FirstOrDefault(l => !labels.Any(x => x.Id == l))
            is { } missing)
        {
            return FilterResult.Conflict("Label no longer exists", $"The filter's label '{missing}' no longer exists in Gmail.");
        }

        var rows = await CreateGroupAsync(tx, parts, ct);
        var names = labels.ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
        return new FilterResult(FilterOutcome.Ok, FilterSnapshot.ToDto(rows, names));
    }

    /// <summary>
    /// Moves filter <paramref name="id"/> from label <paramref name="fromLabelId"/> to <paramref name="toLabelId"/> (a label
    /// merge): creates the retargeted filter first (row <c>restored_from</c> = <paramref name="id"/>), then deletes the
    /// original. A run that died between the two finds the new row and only deletes. NotFound when the original is
    /// already deleted.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    /// <exception cref="GoogleApiException">Gmail refused the new filter or the delete.</exception>
    public async Task<FilterResult> RetargetAsync(string id, string fromLabelId, string toLabelId, CancellationToken ct)
    {
        await using (var tx = await LockAsync(ct))
        {
            var row = await db.Filters.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct);
            if (row is null)
            {
                return new FilterResult(FilterOutcome.NotFound);
            }

            if (!await db.Filters.AnyAsync(r => r.RestoredFrom == id && r.DeletedAt == null, ct))
            {
                var action = row.ReadAction();
                if (!string.IsNullOrEmpty(action.Forward))
                {
                    return FilterResult.Conflict("Filter forwards mail", "The app never creates a forwarding filter.");
                }

                if (await LimitReachedAsync(ct) is { } full)
                {
                    return full;
                }

                var retargeted = new GmailFilterAction(Swap(action.AddLabelIds), Swap(action.RemoveLabelIds));
                await AddRowsAsync(tx, [(await gmail.CreateFilterAsync(row.ReadCriteria(), retargeted, ct), id)], row.GroupId);
            }
            else
            {
                await tx.CommitAsync(ct);
            }
        }

        var deleted = await DeleteAsync(id, ct);
        return deleted.Outcome == FilterOutcome.Conflict ? new FilterResult(FilterOutcome.Ok) : deleted;

        string[] Swap(IReadOnlyList<string> ids) =>
            [.. ids.Select(l => string.Equals(l, fromLabelId, StringComparison.Ordinal) ? toLabelId : l).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>A conflict when <paramref name="adding"/> more filters, less <paramref name="deletesAfter"/>, would pass Gmail's limit.</summary>
    private async Task<FilterResult?> LimitReachedAsync(CancellationToken ct, int deletesAfter = 0, int adding = 1) =>
        await db.Filters.CountAsync(r => r.DeletedAt == null, ct) - deletesAfter + adding > FilterSnapshot.GmailFilterLimit
            ? FilterResult.Conflict(
                "Filter limit reached", $"Gmail allows at most {FilterSnapshot.GmailFilterLimit} filters; delete one first.")
            : null;

    /// <summary>
    /// Creates <paramref name="parts"/> in Gmail, then their rows, one group when there are several (#557). When a part
    /// fails, the ones created before it are deleted again; one Gmail would not delete keeps its row, so it stays listed.
    /// </summary>
    private async Task<List<FilterRow>> CreateGroupAsync(IDbContextTransaction tx, IReadOnlyList<Part> parts, CancellationToken ct)
    {
        var group = parts.Count > 1 ? Guid.CreateVersion7(time.GetUtcNow()) : (Guid?)null;
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
                catch (Exception ex) when (ex is GmailNotConnectedException or GmailRateLimitedException or GoogleApiException)
                {
                    left.Add(c);
                }
            }

            if (left.Count > 0)
            {
                await AddRowsAsync(tx, left, group);
            }

            throw;
        }

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

    /// <summary>Adds a label created for the filter to its batch (inserted with the first), as ApplyActionsJob does.</summary>
    private async Task RecordCreatedAsync(ActionBatchRow batch, GmailLabel label)
    {
        if (batch.CreatedLabelIds.Length == 0)
        {
            db.ActionBatches.Add(batch);
        }

        batch.CreatedLabelIds = [.. batch.CreatedLabelIds, label.Id];
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Commits the label batch of a create that failed after creating labels, unless the rows of a group's leftover
    /// members already committed it.
    /// </summary>
    private async Task KeepCreatedLabelsAsync(IDbContextTransaction tx, ActionBatchRow batch)
    {
        if (batch.CreatedLabelIds.Length > 0 && db.Database.CurrentTransaction is not null)
        {
            await tx.CommitAsync(CancellationToken.None);
        }
    }

    /// <summary>Runs the first sync when the snapshot was never synced, so the limit check counts real filters.</summary>
    private async Task EnsureSyncedAsync(CancellationToken ct)
    {
        var syncedAt = await db.FetchState.AsNoTracking()
            .Where(s => s.Id == FetchStateRow.SingletonId)
            .Select(s => s.FiltersSyncedAt)
            .SingleOrDefaultAsync(ct);
        if (syncedAt is null)
        {
            await snapshot.SyncAsync(ct);
        }
    }

    private async Task<IDbContextTransaction> LockAsync(CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({FilterSnapshot.SyncLockKey})", ct);
        return tx;
    }

    /// <summary>Why Gmail could not answer a preview call; null for an exception the preview must not hide.</summary>
    private static string? GmailWarning(Exception ex) => ex switch
    {
        GmailNotConnectedException => "Gmail is not connected",
        GmailRateLimitedException => "Gmail is rate-limiting requests",
        GoogleApiException or ArgumentException => "Gmail could not run the search",
        _ => null,
    };

    /// <summary>Each path and parent path the mailbox lacks, in creation order.</summary>
    private static List<string> MissingLabels(IReadOnlyList<GmailLabel> labels, IEnumerable<string> paths)
    {
        var missing = new List<string>();
        foreach (var path in paths)
        {
            for (var i = path.IndexOf('/'); ; i = path.IndexOf('/', i + 1))
            {
                var prefix = i < 0 ? path : path[..i];
                if (GmailLabel.FindByName(labels, prefix) is null && !missing.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                {
                    missing.Add(prefix);
                }

                if (i < 0)
                {
                    break;
                }
            }
        }

        return missing;
    }

    /// <summary>One Gmail filter to create, and the deleted filter it restores.</summary>
    private sealed record Part(GmailFilterCriteria Criteria, GmailFilterAction Action, string? RestoredFrom);
}
