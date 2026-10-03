using System.Linq.Expressions;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using GroupOutcome = GmailOrganiser.Review.GroupOutcome;

namespace GmailOrganiser.Claude;

public enum ExternalReviewResult
{
    Ok,
    NotFound,

    /// <summary>The item is not in a status that allows the change.</summary>
    Conflict,

    /// <summary>The target's suggestions are no longer pending.</summary>
    AlreadyDecided,

    /// <summary>Claude asked for a human; there is nothing to accept.</summary>
    NeedsHuman,

    /// <summary>The stored alternative's label is not a valid label path.</summary>
    InvalidVerdict,
}

public enum CreateExternalReviewsResult
{
    Ok,
    RunNotFound,
    TooManyTargets,
}

public enum ReviewVerdictResult
{
    Ok,
    NotFound,
    AlreadyReviewed,

    /// <summary>Cancelled or unavailable: nobody is waiting for the verdict.</summary>
    Closed,
    Invalid,
}

/// <summary>
/// The Claude review queue. Items never touch Gmail; accepting a verdict decides the local suggestions through
/// <see cref="ReviewService"/>, so the decision (memory) rows are written as for any edit or approve. Creating and
/// re-queueing take a table lock so a target never gets two open items.
/// </summary>
public sealed class ExternalReviewService(
    AppDbContext db,
    ReviewService review,
    ExternalReviewQuery query,
    IClaudeReviewStarter starter,
    TimeProvider time)
{
    /// <summary>Most targets one create request may expand to.</summary>
    public const int MaxTargets = 200;

    public const int MaxReasoningLength = 4000;
    public const int MaxErrorLength = 4000;
    public const int MaxFilterCriteriaLength = 4000;
    public const int MaxModelLength = 200;

    public static readonly string[] Reviewers = ["claude_code", "claude_desktop", "mcp"];

    private static readonly Expression<Func<ExternalReviewRow, bool>> IsOpen = r =>
        r.Status == ExternalReviewStatus.Queued
        || r.Status == ExternalReviewStatus.Running
        || (r.Status == ExternalReviewStatus.Reviewed && r.Resolution == ExternalReviewResolution.None);

    /// <summary>
    /// One <c>Queued</c> item per target without an open item whose suggestions are pending; the rest count as
    /// skipped. <paramref name="groups"/> must be normalised. Starts the reviewer when anything was queued.
    /// </summary>
    public async Task<(CreateExternalReviewsResult Result, CreateExternalReviewsResponse? Response)> CreateAsync(
        Guid[] suggestionIds, GroupRef[] groups, Guid? runId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);

        var ids = suggestionIds.Distinct().ToList();
        var groupTargets = groups.Select(g => (g.SenderAddress, g.GroupKey, RunId: (Guid?)null))
            .DistinctBy(g => (g.SenderAddress, g.GroupKey)).ToList();
        if (runId is { } run)
        {
            if (!await db.AnalysisRuns.AnyAsync(r => r.Id == run, ct))
            {
                return (CreateExternalReviewsResult.RunNotFound, null);
            }

            var pending = db.Suggestions.AsNoTracking().Where(s => s.RunId == run && s.Status == SuggestionStatus.Pending);
            var runGroups = await pending.Where(s => s.GroupKey != null)
                .Select(s => new { s.SenderAddress, s.GroupKey }).Distinct().Take(MaxTargets + 1).ToListAsync(ct);
            groupTargets = [.. groupTargets.Concat(runGroups.Select(g => (g.SenderAddress, GroupKey: g.GroupKey!, RunId: runId)))
                .DistinctBy(g => (g.SenderAddress, g.GroupKey))];
            ids = [.. ids.Union(await pending.Where(s => s.GroupKey == null).Select(s => s.Id).Take(MaxTargets + 1).ToListAsync(ct))];
        }

        if (ids.Count + groupTargets.Count > MaxTargets)
        {
            return (CreateExternalReviewsResult.TooManyTargets, null);
        }

        var suggestions = await db.Suggestions.AsNoTracking()
            .Where(s => ids.Contains(s.Id) && s.Status == SuggestionStatus.Pending)
            .ToDictionaryAsync(s => s.Id, ct);
        var openSuggestions = (await db.ExternalReviews.Where(IsOpen)
                .Where(r => r.TargetType == ExternalReviewTarget.Suggestion && ids.Contains(r.SuggestionId!.Value))
                .Select(r => r.SuggestionId!.Value).ToListAsync(ct))
            .ToHashSet();
        var keys = groupTargets.Select(g => g.GroupKey).ToList();
        var pendingGroups = (await db.Suggestions.AsNoTracking()
                .Where(s => keys.Contains(s.GroupKey!) && s.Status == SuggestionStatus.Pending)
                .Select(s => new { s.SenderAddress, s.GroupKey }).Distinct().ToListAsync(ct))
            .Select(g => (g.SenderAddress, g.GroupKey!)).ToHashSet();
        var openGroups = (await db.ExternalReviews.Where(IsOpen)
                .Where(r => r.TargetType == ExternalReviewTarget.Group && keys.Contains(r.GroupKey!))
                .Select(r => new { r.SenderAddress, r.GroupKey }).ToListAsync(ct))
            .Select(g => (g.SenderAddress, g.GroupKey!)).ToHashSet();

        var now = time.GetUtcNow();
        var created = new List<ExternalReviewRow>();
        foreach (var id in ids)
        {
            if (suggestions.TryGetValue(id, out var s) && !openSuggestions.Contains(id))
            {
                created.Add(New(ExternalReviewTarget.Suggestion, s.SenderAddress, s.GroupKey, s.Id, runId is not null && s.RunId == runId ? runId : null, now));
            }
        }

        foreach (var g in groupTargets)
        {
            if (pendingGroups.Contains((g.SenderAddress, g.GroupKey)) && !openGroups.Contains((g.SenderAddress, g.GroupKey)))
            {
                created.Add(New(ExternalReviewTarget.Group, g.SenderAddress, g.GroupKey, null, g.RunId, now));
            }
        }

        db.ExternalReviews.AddRange(created);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (created.Count > 0)
        {
            await starter.StartAsync(ct);
        }

        var skipped = ids.Count + groupTargets.Count - created.Count;
        return (CreateExternalReviewsResult.Ok, new CreateExternalReviewsResponse(created.Count, skipped, await query.ToDtosAsync(created, ct)));
    }

    /// <summary><c>Queued → Cancelled</c>.</summary>
    public Task<(ExternalReviewResult Result, ExternalReviewDto? Item)> CancelAsync(Guid id, CancellationToken ct) =>
        UpdateAsync(id, r => r.Status == ExternalReviewStatus.Queued, s => s.SetProperty(r => r.Status, ExternalReviewStatus.Cancelled), ct);

    /// <summary><c>Reviewed</c> without a resolution <c>→ Dismissed</c>; the local suggestion is unaffected.</summary>
    public Task<(ExternalReviewResult Result, ExternalReviewDto? Item)> DismissAsync(Guid id, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return UpdateAsync(id, r => r.Status == ExternalReviewStatus.Reviewed && r.Resolution == ExternalReviewResolution.None, s => s
            .SetProperty(r => r.Resolution, ExternalReviewResolution.Dismissed)
            .SetProperty(r => r.ResolvedAt, now), ct);
    }

    /// <summary>
    /// <c>Unavailable|Cancelled → Queued</c> while the target is still pending and has no other open item; then starts
    /// the reviewer.
    /// </summary>
    public async Task<(ExternalReviewResult Result, ExternalReviewDto? Item)> RetryAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);
        var row = await db.ExternalReviews.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
        {
            return (ExternalReviewResult.NotFound, null);
        }

        if (row.Status is not (ExternalReviewStatus.Unavailable or ExternalReviewStatus.Cancelled)
            || await db.ExternalReviews.Where(IsOpen).AnyAsync(r => r.Id != id && r.TargetType == row.TargetType
                && (row.TargetType == ExternalReviewTarget.Suggestion
                    ? r.SuggestionId == row.SuggestionId
                    : r.SenderAddress == row.SenderAddress && r.GroupKey == row.GroupKey), ct))
        {
            return (ExternalReviewResult.Conflict, (await query.ToDtosAsync([row], ct))[0]);
        }

        if (!await PendingMembers(row).AnyAsync(ct))
        {
            return (ExternalReviewResult.AlreadyDecided, (await query.ToDtosAsync([row], ct))[0]);
        }

        row.Status = ExternalReviewStatus.Queued;
        row.Error = null;
        row.BatchId = null;
        row.StartedAt = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await starter.StartAsync(ct);
        return (ExternalReviewResult.Ok, (await query.ToDtosAsync([row], ct))[0]);
    }

    /// <summary>
    /// Applies Claude's verdict to the local suggestion(s) through <see cref="ReviewService"/>, then marks the item
    /// accepted. <c>agree</c> approves the outcome shown (for a group, its most common pending outcome, as the card);
    /// <c>alternative</c> edits and approves (for a group, every pending member but a protected one being deleted).
    /// </summary>
    public async Task<(ExternalReviewResult Result, ExternalReviewDto? Item)> AcceptAsync(Guid id, CancellationToken ct)
    {
        var row = await db.ExternalReviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
        {
            return (ExternalReviewResult.NotFound, null);
        }

        if (row.Status != ExternalReviewStatus.Reviewed || row.Resolution != ExternalReviewResolution.None || row.Verdict is null)
        {
            return (ExternalReviewResult.Conflict, (await query.ToDtosAsync([row], ct))[0]);
        }

        if (row.Verdict == ReviewVerdict.NeedsHuman)
        {
            return (ExternalReviewResult.NeedsHuman, (await query.ToDtosAsync([row], ct))[0]);
        }

        var alternative = row.Verdict == ReviewVerdict.Alternative ? AlternativeOf(row) : null;
        if (row.Verdict == ReviewVerdict.Alternative && alternative is null)
        {
            return (ExternalReviewResult.InvalidVerdict, (await query.ToDtosAsync([row], ct))[0]);
        }

        var decided = row.TargetType == ExternalReviewTarget.Suggestion
            ? await AcceptSuggestionAsync(row.SuggestionId!.Value, alternative, ct)
            : await AcceptGroupAsync(row.SenderAddress, row.GroupKey!, alternative, ct);
        if (!decided)
        {
            return (ExternalReviewResult.AlreadyDecided, (await query.ToDtosAsync([row], ct))[0]);
        }

        var now = time.GetUtcNow();
        return await UpdateAsync(id, r => r.Status == ExternalReviewStatus.Reviewed && r.Resolution == ExternalReviewResolution.None, s => s
            .SetProperty(r => r.Resolution, ExternalReviewResolution.AcceptedClaude)
            .SetProperty(r => r.ResolvedAt, now), ct);
    }

    /// <summary>
    /// Stores Claude's verdict: <c>Queued|Running → Reviewed</c>. The reasoning is truncated, not rejected; an
    /// <c>alternative</c> needs a valid label path (missing flags count as false).
    /// </summary>
    public async Task<(ReviewVerdictResult Result, string? Reason)> SubmitVerdictAsync(Guid id, ReviewVerdictInput verdict, CancellationToken ct)
    {
        if (Validate(verdict) is { } invalid)
        {
            return (ReviewVerdictResult.Invalid, invalid);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = (await db.ExternalReviews.FromSql($"SELECT * FROM external_reviews WHERE id = {id} FOR UPDATE").ToListAsync(ct))
            .SingleOrDefault();
        switch (row?.Status)
        {
            case null:
                return (ReviewVerdictResult.NotFound, null);
            case ExternalReviewStatus.Reviewed:
                return (ReviewVerdictResult.AlreadyReviewed, null);
            case ExternalReviewStatus.Cancelled or ExternalReviewStatus.Unavailable:
                return (ReviewVerdictResult.Closed, null);
        }

        var alternative = verdict.Verdict == ReviewVerdict.Alternative;
        row.Status = ExternalReviewStatus.Reviewed;
        row.Verdict = verdict.Verdict;
        row.VerdictTopicLabel = alternative ? verdict.TopicLabel!.Trim() : null;
        row.VerdictNeedsAction = alternative ? verdict.NeedsAction ?? false : null;
        row.VerdictToBeDeleted = alternative ? verdict.ToBeDeleted ?? false : null;
        row.VerdictFilterCriteria = string.IsNullOrWhiteSpace(verdict.FilterCriteria) ? null : verdict.FilterCriteria;
        row.Reasoning = Truncate(verdict.Reasoning.Trim(), MaxReasoningLength);
        row.Reviewer = verdict.Reviewer;
        row.ReviewerModel = string.IsNullOrWhiteSpace(verdict.Model) ? null : verdict.Model.Trim();
        row.ReviewedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (ReviewVerdictResult.Ok, null);
    }

    /// <summary><c>Queued → Running</c> for one reviewer run; returns how many items it took.</summary>
    public Task<int> MarkRunningAsync(IReadOnlyCollection<Guid> ids, Guid batchId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return db.ExternalReviews.Where(r => ids.Contains(r.Id) && r.Status == ExternalReviewStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ExternalReviewStatus.Running)
                .SetProperty(r => r.BatchId, batchId)
                .SetProperty(r => r.StartedAt, now), ct);
    }

    /// <summary><c>Queued|Running → Unavailable</c> ("Claude unavailable"); the local suggestions are unaffected.</summary>
    public Task<int> MarkUnavailableAsync(IReadOnlyCollection<Guid> ids, string error, CancellationToken ct)
    {
        var message = Truncate(error, MaxErrorLength);
        return db.ExternalReviews
            .Where(r => ids.Contains(r.Id) && (r.Status == ExternalReviewStatus.Queued || r.Status == ExternalReviewStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ExternalReviewStatus.Unavailable)
                .SetProperty(r => r.Error, message), ct);
    }

    private static string? Validate(ReviewVerdictInput v)
    {
        if (!Enum.IsDefined(v.Verdict))
        {
            return "Unknown verdict.";
        }

        if (string.IsNullOrWhiteSpace(v.Reasoning))
        {
            return "Reasoning is required.";
        }

        if (!Reviewers.Contains(v.Reviewer))
        {
            return $"Reviewer must be one of {string.Join(", ", Reviewers)}.";
        }

        if (v.Model is { Length: > MaxModelLength })
        {
            return $"Model is at most {MaxModelLength} characters.";
        }

        if (v.Verdict == ReviewVerdict.Alternative && !IsValidLabel(v.TopicLabel))
        {
            return $"An alternative needs a label path: up to five '/'-separated parts, at most {GmailLimits.LabelNameMaxLength} characters, not a Gmail system label.";
        }

        if (!string.IsNullOrWhiteSpace(v.FilterCriteria))
        {
            if (v.FilterCriteria.Length > MaxFilterCriteriaLength)
            {
                return $"Filter criteria are at most {MaxFilterCriteriaLength} characters.";
            }

            try
            {
                using var _ = JsonDocument.Parse(v.FilterCriteria);
            }
            catch (JsonException)
            {
                return "Filter criteria must be JSON.";
            }
        }

        return null;
    }

    private static bool IsValidLabel(string? label) =>
        label?.Trim() is { Length: > 0 } l && LabelPath.IsValid(l) && !LabelPath.IsReserved(l);

    /// <summary>Claude's outcome, or null when the stored label is not a valid path any more.</summary>
    private static GroupOutcome? AlternativeOf(ExternalReviewRow row) =>
        IsValidLabel(row.VerdictTopicLabel)
            ? new GroupOutcome(row.VerdictTopicLabel!.Trim(), row.VerdictNeedsAction ?? false, row.VerdictToBeDeleted ?? false)
            : null;

    private async Task<bool> AcceptSuggestionAsync(Guid suggestionId, GroupOutcome? alternative, CancellationToken ct)
    {
        if (!await db.Suggestions.AnyAsync(s => s.Id == suggestionId && s.Status == SuggestionStatus.Pending, ct))
        {
            return false;
        }

        var (result, _) = alternative is { } o
            ? await review.EditAsync(suggestionId, o.TopicLabel, o.NeedsAction, o.ToBeDeleted, ct)
            : await review.DecideAsync(suggestionId, DecisionOutcome.Approved, ct);
        return result == ReviewResult.Ok;
    }

    private async Task<bool> AcceptGroupAsync(string senderAddress, string groupKey, GroupOutcome? alternative, CancellationToken ct)
    {
        var outcomes = await db.Suggestions.AsNoTracking()
            .Where(s => s.SenderAddress == senderAddress && s.GroupKey == groupKey && s.Status == SuggestionStatus.Pending)
            .GroupBy(s => new { s.TopicLabel, s.NeedsAction, s.ToBeDeleted })
            .Select(g => new { g.Key.TopicLabel, g.Key.NeedsAction, g.Key.ToBeDeleted, Count = g.Count(), HasLlm = g.Any(s => s.Source == SuggestionSource.Llm) })
            .ToListAsync(ct);
        if (outcomes.Count == 0)
        {
            return false;
        }

        if (alternative is { } o)
        {
            await review.EditGroupAsync(senderAddress, groupKey, o, ct);
            return true;
        }

        // The card's outcome, ordered as ReviewQuery orders it.
        var shown = outcomes.OrderByDescending(x => x.Count).ThenByDescending(x => x.HasLlm).ThenBy(x => x.TopicLabel, StringComparer.Ordinal).First();
        await review.DecideGroupAsync(senderAddress, groupKey, DecisionOutcome.Approved, new GroupOutcome(shown.TopicLabel, shown.NeedsAction, shown.ToBeDeleted), ct);
        return true;
    }

    /// <summary>A conditional update: Conflict when the row exists but <paramref name="allowed"/> does not hold.</summary>
    private async Task<(ExternalReviewResult Result, ExternalReviewDto? Item)> UpdateAsync(
        Guid id,
        Expression<Func<ExternalReviewRow, bool>> allowed,
        Action<UpdateSettersBuilder<ExternalReviewRow>> set,
        CancellationToken ct)
    {
        var changed = await db.ExternalReviews.Where(r => r.Id == id).Where(allowed).ExecuteUpdateAsync(set, ct);
        var item = await query.GetAsync(id, ct);
        return item is null
            ? (ExternalReviewResult.NotFound, null)
            : (changed == 1 ? ExternalReviewResult.Ok : ExternalReviewResult.Conflict, item);
    }

    private IQueryable<SuggestionRow> PendingMembers(ExternalReviewRow row) =>
        db.Suggestions.Where(s => s.Status == SuggestionStatus.Pending && (row.TargetType == ExternalReviewTarget.Suggestion
            ? s.Id == row.SuggestionId
            : s.SenderAddress == row.SenderAddress && s.GroupKey == row.GroupKey));

    /// <summary>Serialises creates and re-queues (reads and verdict updates still run).</summary>
    private Task LockAsync(CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("LOCK TABLE external_reviews IN SHARE ROW EXCLUSIVE MODE", ct);

    private static ExternalReviewRow New(
        ExternalReviewTarget type, string senderAddress, string? groupKey, Guid? suggestionId, Guid? runId, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            TargetType = type,
            SuggestionId = suggestionId,
            SenderAddress = senderAddress,
            GroupKey = groupKey,
            RunId = runId,
            Status = ExternalReviewStatus.Queued,
            Resolution = ExternalReviewResolution.None,
            CreatedAt = now,
        };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
