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
    LabelCatalog catalog,
    ISettingsStore settings,
    TimeProvider time,
    ILogger<FilterReviewService> logger)
{
    /// <summary>At most this many filters that need their own count query are counted per review.</summary>
    public const int MaxCountedFilters = 200;

    /// <summary>The <c>pg_advisory_xact_lock</c> key that serialises review creation.</summary>
    private const long CreateLockKey = 0x6672_7276_7720;

    /// <summary>The <c>pg_advisory_lock</c> key that serialises finding applies.</summary>
    private const long ApplyLockKey = 0x6672_6170_706C;

    /// <summary>
    /// Syncs the snapshot, refreshes the labels, checks the active filters and stores the findings; open findings of
    /// earlier reviews become superseded. Every Gmail read happens before the first write.
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
        var now = time.GetUtcNow();
        var counts = await RecentCountsAsync(active, now.AddDays(-days), ct);
        var drafts = FilterChecks.Run(active, labels, r => counts.TryGetValue(r.Id, out var c) ? c : null, days);

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
        await db.FilterFindings.Where(f => f.Status == FilterFindingStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, FilterFindingStatus.Superseded), ct);
        db.FilterReviews.Add(review);
        db.FilterFindings.AddRange(findings);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToDtoAsync(review, findings, Names(labels), ct);
    }

    /// <summary>The newest review, or null.</summary>
    public async Task<FilterReviewDto?> LatestAsync(CancellationToken ct) =>
        await db.FilterReviews.AsNoTracking().OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync(ct)
            is { } review
            ? await ToDtoAsync(review, null, null, ct)
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
    public async Task<FindingResult> ApplyAsync(Guid id, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({ApplyLockKey})", ct);
            try
            {
                return await ApplyLockedAsync(id, ct);
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

    /// <summary>Marks an open finding dismissed; 409 otherwise.</summary>
    public async Task<FindingResult> DismissAsync(Guid id, CancellationToken ct)
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

        finding.Status = FilterFindingStatus.Dismissed;
        await db.SaveChangesAsync(ct);
        return new FindingResult(FilterOutcome.Ok, await ToFindingDtoAsync(finding, ct));
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
        var referenced = finding.FilterIds.Concat(fix.DeleteFilterIds).Except(finding.DeletedFilterIds).ToList();
        var rows = await db.Filters.AsNoTracking().Where(r => referenced.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        if (referenced.FirstOrDefault(i => !rows.TryGetValue(i, out var r) || r.DeletedAt is not null) is { } gone)
        {
            return FindingResult.Conflict("Filter already deleted", $"Filter '{gone}' no longer exists; run a new review.");
        }

        try
        {
            if (fix.Create is { } create && finding.CreatedFilterId is null)
            {
                // A filter equal to the one to create (from an earlier attempt, or by hand) is used, never deleted.
                var existing = (await db.Filters.AsNoTracking().Where(r => r.DeletedAt == null).ToListAsync(ct))
                    .FirstOrDefault(r => FilterChecks.SameFilter(r.ReadCriteria(), r.ReadAction(), create.Criteria, create.Action));
                if (existing is null)
                {
                    var created = await filters.CreateFromIdsAsync(create.Criteria, create.Action, ct);
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

    private static FindingResult NotOpen(FilterFindingRow finding) =>
        FindingResult.Conflict("Finding is not open", $"The finding is {finding.Status.ToString().ToLowerInvariant()}.");

    /// <summary>
    /// Stored messages since <paramref name="cutoff"/> each locally evaluable filter matches: single <c>from</c> filters
    /// from one grouped query, the rest one count each up to <see cref="MaxCountedFilters"/>.
    /// </summary>
    private async Task<Dictionary<string, int>> RecentCountsAsync(List<FilterRow> active, DateTimeOffset cutoff, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var recent = db.Messages.AsNoTracking().Where(m => !m.DeletedInGmail && m.InternalDate >= cutoff);
        var fromOnly = new List<(FilterRow Row, IReadOnlyList<string> Terms)>();
        var other = new List<(FilterRow Row, Func<IQueryable<MessageRow>, IQueryable<MessageRow>> Filter)>();
        foreach (var row in active)
        {
            var criteria = row.ReadCriteria();
            if (FilterCriteriaMapping.LocalFilter(criteria, out _) is not { } filter)
            {
                continue;
            }

            if (FilterChecks.Normalise(criteria with { From = null }) == FilterChecks.Normalise(new GmailFilterCriteria())
                && FilterCriteriaMapping.FromTerms(criteria.From!) is { } terms)
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
            foreach (var (row, terms) in fromOnly)
            {
                counts[row.Id] = senders.Where(s => terms.Any(t => FilterCriteriaMapping.FromTermMatches(t, s.Key.ToLowerInvariant())))
                    .Sum(s => s.Count);
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
        FilterReviewRow review, List<FilterFindingRow>? findings, Dictionary<string, string>? names, CancellationToken ct)
    {
        findings ??= await db.FilterFindings.AsNoTracking().Where(f => f.ReviewId == review.Id).OrderBy(f => f.Id).ToListAsync(ct);
        names ??= await LabelNamesAsync(ct);
        var ids = findings.SelectMany(f => f.FilterIds).Distinct().ToList();
        var rows = await db.Filters.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        return new FilterReviewDto(
            review.Id, review.CreatedAt, review.FilterCount, [.. findings.Select(f => ToFindingDto(f, rows, names))], review.Summary,
            review.SummaryModel, review.SummarisedAt, review.SummaryError);
    }

    private async Task<FilterFindingDto> ToFindingDtoAsync(FilterFindingRow finding, CancellationToken ct)
    {
        var rows = await db.Filters.AsNoTracking().Where(r => finding.FilterIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        return ToFindingDto(finding, rows, await LabelNamesAsync(ct));
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
            f.Description, new FilterFixDto(fix.Kind, fix.DeleteFilterIds, create), f.Status, f.AppliedAt, f.Error);
    }

    private async Task<Dictionary<string, string>> LabelNamesAsync(CancellationToken ct)
    {
        try
        {
            return Names(await catalog.GetAsync(ct));
        }
        catch (Exception ex) when (ex is GmailNotConnectedException or GmailRateLimitedException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, string> Names(IReadOnlyList<GmailLabel> labels) =>
        labels.ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
}
