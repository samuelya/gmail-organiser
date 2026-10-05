using System.Linq.Expressions;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Settings;
using Google;
using Microsoft.EntityFrameworkCore;
using LabelCatalog = GmailOrganiser.Review.LabelCatalog;

namespace GmailOrganiser.Rules.Review;

public sealed record FindingResult(FilterOutcome Outcome, FilterFindingDto? Finding = null, string? Title = null, string? Detail = null)
{
    public static FindingResult Conflict(string title, string detail) => new(FilterOutcome.Conflict, null, title, detail);
}

/// <summary>
/// Runs filter reviews (DESIGN §6.5) and applies or dismisses their findings. Every Gmail write goes through
/// <see cref="FilterService"/>, so a deleted filter keeps its row and can be restored.
/// </summary>
public sealed class FilterReviewService(
    AppDbContext db,
    FilterSnapshot snapshot,
    FilterService filters,
    PolicyFilterProposalQuery policyProposals,
    LabelCatalog catalog,
    ISettingsStore settings,
    TimeProvider time,
    ILogger<FilterReviewService> logger)
{
    /// <summary>At most this many filters that need their own count query are counted per review.</summary>
    public const int MaxCountedFilters = 200;

    /// <summary>The <c>pg_advisory_xact_lock</c> key that serialises review creation.</summary>
    private const long CreateLockKey = 0x6672_7276_7720;

    /// <summary>The advisory lock key that serialises finding applies, dismissals and supersedes.</summary>
    public const long ApplyLockKey = 0x6672_6170_706C;

    /// <summary>Open findings an apply has started but not finished: it created a filter or deleted one.</summary>
    private static readonly Expression<Func<FilterFindingRow, bool>> InProgress =
        f => f.Status == FilterFindingStatus.Open && (f.CreatedFilterId != null || f.DeletedFilterIds.Count > 0);

    /// <summary>Open findings no apply has touched; a new review supersedes them.</summary>
    private static readonly Expression<Func<FilterFindingRow, bool>> NotStarted =
        f => f.Status == FilterFindingStatus.Open && f.CreatedFilterId == null && f.DeletedFilterIds.Count == 0;

    /// <summary>
    /// Syncs the snapshot, refreshes the labels, checks the active filters and stores the findings; open findings of
    /// earlier reviews become superseded, except half-applied ones (<see cref="IsStarted"/>), whose filters the
    /// new review leaves out and which the returned review carries over. Every Gmail read happens before the first write.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    /// <exception cref="Jobs.JobRefusedException">The local data belongs to another account.</exception>
    public async Task<FilterReviewDto> CreateAsync(CancellationToken ct)
    {
        await snapshot.SyncAsync(ct);
        var labels = await catalog.RefreshAsync(ct);
        var days = (await settings.GetAsync(ct)).RulesStaleFilterDays;
        var active = await db.Filters.AsNoTracking().Where(r => r.DeletedAt == null).ToListAsync(ct);
        var busy = (await db.FilterFindings.AsNoTracking().Where(InProgress)
                .Select(f => new { f.FilterIds, f.CreatedFilterId }).ToListAsync(ct))
            .SelectMany(f => f.CreatedFilterId is { } created ? f.FilterIds.Append(created) : f.FilterIds)
            .ToHashSet(StringComparer.Ordinal);
        var checkedFilters = active.Where(r => !busy.Contains(r.Id)).ToList();
        var now = time.GetUtcNow();
        var counts = await RecentCountsAsync(checkedFilters, now.AddDays(-days), ct);
        var proposals = await policyProposals.ListAsync(ct, includeCovered: true);
        var drafts = FilterChecks.Run(checkedFilters, labels, r => counts.TryGetValue(r.Id, out var c) ? c : null, days, proposals);

        var review = new FilterReviewRow
        {
            Id = Guid.CreateVersion7(now),
            CreatedAt = now,
            FilterCount = active.Count,
            FindingCount = drafts.Count,
        };
        var findings = drafts.Select((d, i) =>
        {
            var row = new FilterFindingRow
            {
                // One millisecond apart, so the id order is the checks' order.
                Id = Guid.CreateVersion7(now.AddMilliseconds(i)),
                ReviewId = review.Id,
                Kind = d.Kind,
                FilterIds = [.. d.FilterIds],
                Description = d.Description,
                Status = FilterFindingStatus.Open,
            };
            row.WriteFix(d.Fix);
            return row;
        }).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({CreateLockKey})", ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ApplyLockKey})", ct);
        await db.FilterFindings.Where(NotStarted)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, FilterFindingStatus.Superseded), ct);
        db.FilterReviews.Add(review);
        db.FilterFindings.AddRange(findings);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(review, findings, Names(labels), ct, carryOver: true);
    }

    /// <summary>The newest review, or null; it carries over the half-applied findings of earlier reviews.</summary>
    public async Task<FilterReviewDto?> LatestAsync(CancellationToken ct) =>
        await db.FilterReviews.AsNoTracking().OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync(ct)
            is { } review
            ? await ToDtoAsync(review, null, null, ct, carryOver: true)
            : null;

    public async Task<FilterReviewDto?> GetAsync(Guid id, CancellationToken ct) =>
        await db.FilterReviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) is { } review
            ? await ToDtoAsync(review, null, null, ct)
            : null;

    /// <summary>
    /// Runs an open finding's fix: the create first, then each delete, recording progress on the finding so a re-apply
    /// after a failure neither creates twice nor trips over its own deletes. A failure leaves the finding open with
    /// <see cref="FilterFindingRow.Error"/> and is rethrown (Gmail exceptions) or returned (conflicts).
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    /// <exception cref="GoogleApiException">Gmail refused a create or delete.</exception>
    public Task<FindingResult> ApplyAsync(Guid id, CancellationToken ct) => UnderApplyLockAsync(() => ApplyLockedAsync(id, ct), ct);

    /// <summary>Marks an open finding dismissed; 409 when it is not open or is half applied (only a re-apply finishes it).</summary>
    public Task<FindingResult> DismissAsync(Guid id, CancellationToken ct) => UnderApplyLockAsync(async () =>
    {
        var finding = await db.FilterFindings.SingleOrDefaultAsync(f => f.Id == id, ct);
        if (finding is null)
        {
            return new FindingResult(FilterOutcome.NotFound);
        }

        if (finding.Status != FilterFindingStatus.Open)
        {
            return NotOpen(finding);
        }

        if (IsStarted(finding))
        {
            return FindingResult.Conflict(
                "Finding is half applied", "An apply has changed some of its filters but not all; apply it again to finish.");
        }

        finding.Status = FilterFindingStatus.Dismissed;
        await db.SaveChangesAsync(ct);
        return new FindingResult(FilterOutcome.Ok, await ToFindingDtoAsync(finding, ct));
    }, ct);

    private async Task<FindingResult> UnderApplyLockAsync(Func<Task<FindingResult>> action, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({ApplyLockKey})", ct);
            try
            {
                return await action();
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({ApplyLockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<FindingResult> ApplyLockedAsync(Guid id, CancellationToken ct)
    {
        var finding = await db.FilterFindings.SingleOrDefaultAsync(f => f.Id == id, ct);
        if (finding is null)
        {
            return new FindingResult(FilterOutcome.NotFound);
        }

        if (finding.Status != FilterFindingStatus.Open)
        {
            return NotOpen(finding);
        }

        var fix = finding.ReadFix();
        if (fix.Kind == FilterFixKind.None)
        {
            return FindingResult.Conflict("Finding has no fix", "Review these filters by hand in Gmail.");
        }

        var referenced = finding.FilterIds.Concat(fix.DeleteFilterIds).Except(finding.DeletedFilterIds).ToList();
        var rows = await db.Filters.AsNoTracking().Where(r => referenced.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        if (referenced.FirstOrDefault(i => !rows.TryGetValue(i, out var r) || r.DeletedAt is not null) is { } gone)
        {
            return FindingResult.Conflict("Filter already deleted", $"Filter '{gone}' no longer exists; run a new review.");
        }

        // A filter an earlier attempt created that is gone since (deleted by hand, or through the app) is created again.
        if (finding.CreatedFilterId is { } createdId
            && !await db.Filters.AnyAsync(r => r.Id == createdId && r.DeletedAt == null, ct))
        {
            finding.CreatedFilterId = null;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var pendingDeletes = fix.DeleteFilterIds.Except(finding.DeletedFilterIds).Count();
        try
        {
            if (fix.Create is { } create && finding.CreatedFilterId is null)
            {
                // A filter equal to the one to create (from an earlier attempt, or by hand) is used, never deleted.
                var existing = (await db.Filters.AsNoTracking().Where(r => r.DeletedAt == null).ToListAsync(ct))
                    .FirstOrDefault(r => FilterChecks.SameFilter(r.ReadCriteria(), r.ReadAction(), create.Criteria, create.Action));
                if (existing is null)
                {
                    var created = await filters.CreateFromIdsAsync(create.Criteria, create.Action, pendingDeletes, ct);
                    if (created is not { Outcome: FilterOutcome.Ok, Filter: { } filter })
                    {
                        await RecordErrorAsync(finding, $"{created.Title}: {created.Detail}");
                        return FindingResult.Conflict(created.Title ?? "Filter not created", created.Detail ?? "");
                    }

                    finding.CreatedFilterId = filter.Id;
                }
                else
                {
                    finding.CreatedFilterId = existing.Id;
                }

                await db.SaveChangesAsync(CancellationToken.None);
            }

            var keep = finding.CreatedFilterId;
            foreach (var filterId in fix.DeleteFilterIds.Except(finding.DeletedFilterIds).Where(i => i != keep).ToList())
            {
                // NotFound or "already deleted" here means a concurrent sync saw the filter gone: the delete is done.
                await filters.DeleteAsync(filterId, ct);
                finding.DeletedFilterIds = [.. finding.DeletedFilterIds, filterId];
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is GmailNotConnectedException or GmailRateLimitedException or GoogleApiException)
        {
            await RecordErrorAsync(finding, ex is GoogleApiException g ? g.Error?.Message ?? g.Message : ex.Message);
            throw;
        }

        finding.Status = FilterFindingStatus.Applied;
        finding.AppliedAt = time.GetUtcNow();
        finding.Error = null;
        await db.SaveChangesAsync(CancellationToken.None);
        return new FindingResult(FilterOutcome.Ok, await ToFindingDtoAsync(finding, CancellationToken.None));
    }

    private async Task RecordErrorAsync(FilterFindingRow finding, string error)
    {
        finding.Error = error;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Whether an apply has created or deleted a filter for the finding; only a re-apply finishes it then.</summary>
    private static bool IsStarted(FilterFindingRow finding) => finding.CreatedFilterId is not null || finding.DeletedFilterIds.Count > 0;

    private static FindingResult NotOpen(FilterFindingRow finding) =>
        FindingResult.Conflict("Finding is not open", $"The finding is {finding.Status.ToString().ToLowerInvariant()}.");

    /// <summary>
    /// Stored messages since <paramref name="cutoff"/> each locally evaluable filter matches: <c>from</c>-only filters
    /// from one grouped query, the rest one count each up to <see cref="MaxCountedFilters"/>. None when the stored mail
    /// does not cover the whole window (the mailbox fetch has not completed, or the oldest message is newer than the
    /// cutoff), and none for a filter first seen after the cutoff.
    /// </summary>
    private async Task<Dictionary<string, int>> RecentCountsAsync(List<FilterRow> active, DateTimeOffset cutoff, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var state = await db.FetchState.AsNoTracking().SingleOrDefaultAsync(s => s.Id == FetchStateRow.SingletonId, ct);
        var stored = db.Messages.AsNoTracking().Where(m => !m.DeletedInGmail);
        if (state is not { MailboxPhase: MailboxPhase.Completed, LastHistoryId: not null }
            || !await stored.AnyAsync(m => m.InternalDate < cutoff, ct))
        {
            return counts;
        }

        var recent = stored.Where(m => m.InternalDate >= cutoff);
        var fromOnly = new List<(FilterRow Row, IReadOnlyList<string> Terms)>();
        var other = new List<(FilterRow Row, Func<IQueryable<MessageRow>, IQueryable<MessageRow>> Filter)>();
        foreach (var row in active.Where(r => r.FirstSeenAt < cutoff))
        {
            var criteria = row.ReadCriteria();
            if (FilterCriteriaMapping.LocalFilter(criteria, out _) is not { } filter)
            {
                continue;
            }

            if (FilterChecks.FromOnlyTerms(criteria) is { } terms)
            {
                fromOnly.Add((row, terms));
            }
            else
            {
                other.Add((row, filter));
            }
        }

        if (fromOnly.Count > 0)
        {
            var senders = await recent.GroupBy(m => m.FromAddress).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            var index = SenderIndex.Build(senders.Select(s => (s.Key, s.Count)));
            foreach (var (row, terms) in fromOnly)
            {
                counts[row.Id] = index.Count(terms);
            }
        }

        if (other.Count > MaxCountedFilters)
        {
            logger.LogWarning(
                "Filter review counts {Counted} of {Total} filters with non-sender criteria; the rest are not checked for recent matches",
                MaxCountedFilters, other.Count);
        }

        foreach (var (row, filter) in other.Take(MaxCountedFilters))
        {
            counts[row.Id] = await filter(recent).CountAsync(ct);
        }

        return counts;
    }

    private async Task<FilterReviewDto> ToDtoAsync(
        FilterReviewRow review, List<FilterFindingRow>? findings, Dictionary<string, string>? names, CancellationToken ct,
        bool carryOver = false)
    {
        findings ??= await db.FilterFindings.AsNoTracking().Where(f => f.ReviewId == review.Id).OrderBy(f => f.Id).ToListAsync(ct);
        if (carryOver)
        {
            // Half-applied findings of earlier reviews stay open (never superseded), so they are listed here to be resumed.
            findings = [.. findings, .. await db.FilterFindings.AsNoTracking().Where(InProgress)
                .Where(f => f.ReviewId != review.Id).OrderBy(f => f.Id).ToListAsync(ct)];
        }

        names ??= await snapshot.LabelNamesAsync(ct);
        var ids = findings.SelectMany(f => f.FilterIds).Distinct().ToList();
        var rows = await db.Filters.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        return new FilterReviewDto(
            review.Id, review.CreatedAt, review.FilterCount, [.. findings.Select(f => ToFindingDto(f, rows, names))], review.Summary,
            review.SummaryModel, review.SummaryPromptVersion, review.SummarisedAt, review.SummaryError);
    }

    private async Task<FilterFindingDto> ToFindingDtoAsync(FilterFindingRow finding, CancellationToken ct)
    {
        var rows = await db.Filters.AsNoTracking().Where(r => finding.FilterIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        return ToFindingDto(finding, rows, await snapshot.LabelNamesAsync(ct));
    }

    private static FilterFindingDto ToFindingDto(
        FilterFindingRow f, Dictionary<string, FilterRow> rows, IReadOnlyDictionary<string, string> names)
    {
        var fix = f.ReadFix();
        FilterFixCreateDto? create = null;
        if (fix.Create is { } c)
        {
            var dto = FilterSnapshot.ToDto(
                new FilterRow { Criteria = FilterRow.WriteCriteria(c.Criteria), Action = FilterRow.WriteAction(c.Action) }, names);
            create = new FilterFixCreateDto(dto.Criteria, dto.Action);
        }

        return new FilterFindingDto(
            f.Id, f.Kind, f.FilterIds, [.. f.FilterIds.Where(rows.ContainsKey).Select(i => FilterSnapshot.ToDto(rows[i], names))],
            f.Description, new FilterFixDto(fix.Kind, fix.DeleteFilterIds, create), f.Status, f.AppliedAt, f.Error, f.ReviewId,
            fix.PolicyId);
    }

    private static Dictionary<string, string> Names(IReadOnlyList<GmailLabel> labels) =>
        labels.ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
}
