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
    /// Under the lock: the limit check, the labels the action names (each recorded in a
    /// <see cref="ActionKind.FilterLabels"/> batch right after its create), the Gmail filter, then its row
    /// (<c>created_by_app</c>).
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    /// <exception cref="GoogleApiException">Gmail refused the label or the filter (e.g. the filter already exists).</exception>
    /// <exception cref="Jobs.JobRefusedException">The first sync found the local data belongs to another account.</exception>
    public async Task<FilterResult> CreateAsync(FilterSpec spec, CancellationToken ct)
    {
        await EnsureSyncedAsync(ct);
        await using var tx = await LockAsync(ct);
        if (await LimitReachedAsync(ct) is { } full)
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
            var created = await gmail.CreateFilterAsync(spec.Criteria, action, ct);
            return new FilterResult(FilterOutcome.Ok, FilterSnapshot.ToDto(await AddRowAsync(tx, created, null), names));
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

    /// <summary>Deletes the filter in Gmail and marks its row deleted by the app.</summary>
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

        await gmail.DeleteFilterAsync(id, ct);

        // Gmail has changed: the row follows even if the client has gone.
        var now = time.GetUtcNow();
        row.DeletedAt = now;
        row.DeletedByApp = true;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);
        return new FilterResult(FilterOutcome.Ok);
    }

    /// <summary>
    /// Re-creates a deleted filter from its stored criteria and action as a new Gmail filter (new id, new row with
    /// <c>restored_from</c>); the deleted row stays as it is. Every check runs under the lock, so two restores of one
    /// row create one filter.
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

        if (await db.Filters.AnyAsync(r => r.RestoredFrom == id && r.DeletedAt == null, ct))
        {
            return FilterResult.Conflict("Filter already restored", $"Filter '{id}' was already restored as an active filter.");
        }

        var criteria = row.ReadCriteria();
        var action = row.ReadAction();
        if (!string.IsNullOrEmpty(action.Forward))
        {
            return FilterResult.Conflict("Filter forwards mail", "The app never creates a forwarding filter.");
        }

        if (!criteria.MatchesMail || action.IsEmpty)
        {
            return FilterResult.Conflict("Filter cannot be restored", "The stored filter has no criterion or no action.");
        }

        if (await LimitReachedAsync(ct) is { } full)
        {
            return full;
        }

        var labels = await catalog.GetAsync(ct);
        if (action.AddLabelIds.Concat(action.RemoveLabelIds).FirstOrDefault(l => !labels.Any(x => x.Id == l)) is { } missing)
        {
            return FilterResult.Conflict("Label no longer exists", $"The filter's label '{missing}' no longer exists in Gmail.");
        }

        var created = await gmail.CreateFilterAsync(criteria, action, ct);
        var names = labels.ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
        return new FilterResult(FilterOutcome.Ok, FilterSnapshot.ToDto(await AddRowAsync(tx, created, id), names));
    }

    public static FilterCriteriaDto ToCriteriaDto(GmailFilterCriteria c) => new(
        c.From, c.To, c.Subject, c.Query, c.NegatedQuery, c.HasAttachment, c.ExcludeChats, c.Size, c.SizeComparison?.ToGmailString());

    private async Task<FilterResult?> LimitReachedAsync(CancellationToken ct) =>
        await db.Filters.CountAsync(r => r.DeletedAt == null, ct) >= FilterSnapshot.GmailFilterLimit
            ? FilterResult.Conflict(
                "Filter limit reached", $"Gmail allows at most {FilterSnapshot.GmailFilterLimit} filters; delete one first.")
            : null;

    /// <summary>The row of a filter Gmail just created, committed even if the client has gone, so sync never mistakes it.</summary>
    private async Task<FilterRow> AddRowAsync(IDbContextTransaction tx, GmailFilter created, string? restoredFrom)
    {
        var now = time.GetUtcNow();
        var row = new FilterRow
        {
            Id = created.Id,
            Criteria = FilterRow.WriteCriteria(created.Criteria),
            Action = FilterRow.WriteAction(created.Action),
            CriteriaSummary = FilterSnapshot.Summarise(created.Criteria),
            CreatedByApp = true,
            RestoredFrom = restoredFrom,
            FirstSeenAt = now,
            LastSeenAt = now,
            UpdatedAt = now,
        };
        db.Filters.Add(row);
        await db.SaveChangesAsync(CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);
        return row;
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

    /// <summary>Commits the label batch of a create that failed after creating labels; nothing else was written.</summary>
    private static async Task KeepCreatedLabelsAsync(IDbContextTransaction tx, ActionBatchRow batch)
    {
        if (batch.CreatedLabelIds.Length > 0)
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
}
