using System.Linq.Expressions;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Npgsql;
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

    /// <summary>
    /// Pending suggestions are left, but none can take Claude's outcome: it would mark protected mail to-be-deleted, or
    /// (for <c>agree</c>) no member has the outcome Claude reviewed any more.
    /// </summary>
    NotApplicable,
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

    /// <summary>The target's suggestions are no longer pending: the user decided them after sending it to Claude.</summary>
    AlreadyDecided,
}

/// <summary>
/// The Claude review queue. Items never touch Gmail; accepting a verdict decides the local suggestions through
/// <see cref="ReviewService"/>, so the decision (memory) rows are written as for any edit or approve. The partial unique
/// indexes on open items keep a target to one open item; accepting holds the item's row lock while it decides. Every
/// committed status or resolution change is published through <see cref="IExternalReviewNotifier"/>.
/// </summary>
public sealed class ExternalReviewService(
    AppDbContext db,
    ReviewService review,
    ReviewQuery reviewQuery,
    DecisionRecorder decisions,
    ExternalReviewQuery query,
    IClaudeReviewStarter starter,
    IExternalReviewNotifier notifier,
    TimeProvider time)
{
    /// <summary>Most targets one create request may expand to.</summary>
    public const int MaxTargets = 200;

    public const int MaxReasoningLength = 4000;
    public const int MaxErrorLength = 4000;
    public const int MaxFilterCriteriaLength = 4000;
    public const int MaxModelLength = 200;

    public static readonly string[] Reviewers = ["claude_code", "claude_desktop", "mcp"];

    /// <summary>
    /// One <c>Queued</c> item per target without an open item whose suggestions are pending; the rest count as
    /// skipped. <paramref name="groups"/> must be normalised. Starts the reviewer when anything was queued.
    /// </summary>
    public async Task<(CreateExternalReviewsResult Result, CreateExternalReviewsResponse? Response)> CreateAsync(
        Guid[] suggestionIds, GroupRef[] groups, Guid? runId, CancellationToken ct)
    {
        var ids = suggestionIds.Distinct().ToList();
        var groupTargets = groups.Select(g => (g.SenderAddress, g.GroupKey, RunId: (Guid?)null)).ToList();
        if (runId is { } run)
        {
            if (!await db.AnalysisRuns.AnyAsync(r => r.Id == run, ct))
            {
                return (CreateExternalReviewsResult.RunNotFound, null);
            }

            var pending = db.Suggestions.AsNoTracking().Where(s => s.RunId == run && s.Status == SuggestionStatus.Pending);
            var runGroups = await pending.Where(s => s.GroupKey != null)
                .Select(s => new { s.SenderAddress, s.GroupKey }).Distinct().Take(MaxTargets + 1).ToListAsync(ct);

            // The run's entries first, so a group also named explicitly keeps the run id.
            groupTargets = [.. runGroups.Select(g => (g.SenderAddress, GroupKey: g.GroupKey!, RunId: runId)).Concat(groupTargets)];
            ids = [.. ids.Union(await pending.Where(s => s.GroupKey == null).Select(s => s.Id).Take(MaxTargets + 1).ToListAsync(ct))];
        }

        groupTargets = [.. groupTargets.DistinctBy(g => (g.SenderAddress, g.GroupKey))];
        if (ids.Count + groupTargets.Count > MaxTargets)
        {
            return (CreateExternalReviewsResult.TooManyTargets, null);
        }

        var suggestions = await db.Suggestions.AsNoTracking()
            .Where(s => ids.Contains(s.Id) && s.Status == SuggestionStatus.Pending)
            .ToDictionaryAsync(s => s.Id, ct);
        var senders = groupTargets.Select(g => g.SenderAddress).Distinct().ToList();
        var keys = groupTargets.Select(g => g.GroupKey).Distinct().ToList();
        var pendingGroups = (await db.Suggestions.AsNoTracking()
                .Where(s => senders.Contains(s.SenderAddress) && keys.Contains(s.GroupKey!) && s.Status == SuggestionStatus.Pending)
                .Select(s => new { s.SenderAddress, s.GroupKey }).Distinct().ToListAsync(ct))
            .Select(g => (g.SenderAddress, g.GroupKey!)).ToHashSet();

        var now = time.GetUtcNow();
        var candidates = new List<ExternalReviewRow>();
        foreach (var id in ids)
        {
            if (suggestions.TryGetValue(id, out var s))
            {
                candidates.Add(New(ExternalReviewTarget.Suggestion, s.SenderAddress, s.GroupKey, s.Id, runId is not null && s.RunId == runId ? runId : null, now));
            }
        }

        foreach (var g in groupTargets)
        {
            if (pendingGroups.Contains((g.SenderAddress, g.GroupKey)))
            {
                candidates.Add(New(ExternalReviewTarget.Group, g.SenderAddress, g.GroupKey, null, g.RunId, now));
            }
        }

        var created = await InsertWithoutOpenAsync(candidates, ct);
        var items = await query.ToDtosAsync(created, ct);
        await NotifyAsync(items, ct);
        if (created.Count > 0)
        {
            await starter.StartAsync(ct);
        }

        var skipped = ids.Count + groupTargets.Count - created.Count;
        return (CreateExternalReviewsResult.Ok, new CreateExternalReviewsResponse(created.Count, skipped, items));
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
        var row = await db.ExternalReviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
        {
            return (ExternalReviewResult.NotFound, null);
        }

        if (row.Status is (ExternalReviewStatus.Unavailable or ExternalReviewStatus.Cancelled) && !await PendingMembers(row).AnyAsync(ct))
        {
            return (ExternalReviewResult.AlreadyDecided, (await query.ToDtosAsync([row], ct))[0]);
        }

        // Another open item for the target makes the update a unique violation, reported as a conflict.
        var result = await UpdateAsync(id, r => r.Status == ExternalReviewStatus.Unavailable || r.Status == ExternalReviewStatus.Cancelled, s => s
            .SetProperty(r => r.Status, ExternalReviewStatus.Queued)
            .SetProperty(r => r.Error, (string?)null)
            .SetProperty(r => r.BatchId, (Guid?)null)
            .SetProperty(r => r.StartedAt, (DateTimeOffset?)null), ct);
        if (result.Result == ExternalReviewResult.Ok)
        {
            await starter.StartAsync(ct);
        }

        return result;
    }

    /// <summary>
    /// Applies Claude's verdict to the still-pending local suggestion(s) through <see cref="ReviewService"/> and marks the
    /// item accepted, in one transaction holding the item's row lock, so a second accept, a dismiss or a user decision
    /// in between never overwrites a decision or records one twice. <c>agree</c> approves the outcome Claude reviewed;
    /// <c>alternative</c> edits and approves (never a to-be-deleted edit of protected mail).
    /// </summary>
    public async Task<(ExternalReviewResult Result, ExternalReviewDto? Item)> AcceptAsync(Guid id, CancellationToken ct)
    {
        ExternalReviewResult result;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var row = (await db.ExternalReviews.FromSql($"SELECT * FROM external_reviews WHERE id = {id} FOR UPDATE")
                .AsNoTracking().ToListAsync(ct)).SingleOrDefault();
            result = row is null ? ExternalReviewResult.NotFound : await DecideAsync(row, ct);
            if (result == ExternalReviewResult.Ok)
            {
                var now = time.GetUtcNow();
                await db.ExternalReviews.Where(r => r.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Resolution, ExternalReviewResolution.AcceptedClaude)
                    .SetProperty(r => r.ResolvedAt, now), ct);
                await tx.CommitAsync(ct);
                decisions.Committed();
            }
        }

        var item = result == ExternalReviewResult.NotFound ? null : await query.GetAsync(id, ct);
        if (result == ExternalReviewResult.Ok && item is not null)
        {
            await notifier.NotifyAsync(item, ct);
        }

        return (result, item);
    }

    /// <summary>
    /// Stores Claude's verdict: <c>Queued|Running → Reviewed</c> while the target is still pending (otherwise
    /// <c>→ Cancelled</c> and <see cref="ReviewVerdictResult.AlreadyDecided"/>). The reasoning is truncated, not rejected; an
    /// <c>alternative</c> needs a valid label path (missing flags count as false). For <c>agree</c> the outcome shown
    /// now (the pending suggestion's, or the group card's) is stored as the one Claude agreed with.
    /// </summary>
    public async Task<(ReviewVerdictResult Result, string? Reason)> SubmitVerdictAsync(Guid id, ReviewVerdictInput verdict, CancellationToken ct)
    {
        if (ReviewVerdictValidation.Validate(verdict) is { } invalid)
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

        if (!await PendingMembers(row).AnyAsync(ct))
        {
            // The user decided meanwhile: nobody waits for this verdict, so the item is closed rather than left open.
            row.Status = ExternalReviewStatus.Cancelled;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await NotifyAsync(await query.ToDtosAsync([row], ct), ct);
            return (ReviewVerdictResult.AlreadyDecided, null);
        }

        var outcome = verdict.Verdict switch
        {
            ReviewVerdict.Alternative => new GroupOutcome(verdict.TopicLabel!.Trim(), verdict.NeedsAction ?? false, verdict.ToBeDeleted ?? false),
            ReviewVerdict.Agree => await ShownOutcomeAsync(row, ct),
            _ => null,
        };
        row.Status = ExternalReviewStatus.Reviewed;
        row.Verdict = verdict.Verdict;
        row.VerdictTopicLabel = outcome?.TopicLabel;
        row.VerdictNeedsAction = outcome?.NeedsAction;
        row.VerdictToBeDeleted = outcome?.ToBeDeleted;
        row.VerdictFilterCriteria = string.IsNullOrWhiteSpace(verdict.FilterCriteria) ? null : verdict.FilterCriteria;
        row.Reasoning = Truncate(verdict.Reasoning.Trim(), MaxReasoningLength);
        row.Reviewer = verdict.Reviewer;
        row.ReviewerModel = string.IsNullOrWhiteSpace(verdict.Model) ? null : verdict.Model.Trim();
        row.ReviewedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await NotifyAsync(await query.ToDtosAsync([row], ct), ct);
        return (ReviewVerdictResult.Ok, null);
    }

    /// <summary><c>Queued → Running</c> for one reviewer run while the target is still pending (as <c>list_pending_reviews</c>
    /// shows); the rest were decided meanwhile and become <c>Cancelled</c>. Returns how many items it took.</summary>
    public async Task<int> MarkRunningAsync(IReadOnlyCollection<Guid> ids, Guid batchId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var queued = db.ExternalReviews.Where(r => ids.Contains(r.Id) && r.Status == ExternalReviewStatus.Queued);
        var changed = await queued.Where(r => db.Suggestions.Any(s => s.Status == SuggestionStatus.Pending
                && (r.TargetType == ExternalReviewTarget.Suggestion ? s.Id == r.SuggestionId
                    : s.SenderAddress == r.SenderAddress && s.GroupKey == r.GroupKey)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ExternalReviewStatus.Running)
                .SetProperty(r => r.BatchId, batchId).SetProperty(r => r.StartedAt, now), ct);
        var cancelled = await queued.ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ExternalReviewStatus.Cancelled), ct);
        await NotifyChangedAsync(cancelled, r => ids.Contains(r.Id) && r.Status == ExternalReviewStatus.Cancelled, ct);
        return await NotifyChangedAsync(changed, r => ids.Contains(r.Id) && r.Status == ExternalReviewStatus.Running && r.BatchId == batchId, ct);
    }

    /// <summary>
    /// <c>Queued|Running → Unavailable</c> ("Claude unavailable"); the local suggestions are unaffected. The ids that
    /// were unavailable already are published again with the others.
    /// </summary>
    public async Task<int> MarkUnavailableAsync(IReadOnlyCollection<Guid> ids, string error, CancellationToken ct)
    {
        var message = Truncate(error, MaxErrorLength);
        var changed = await db.ExternalReviews
            .Where(r => ids.Contains(r.Id) && (r.Status == ExternalReviewStatus.Queued || r.Status == ExternalReviewStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ExternalReviewStatus.Unavailable)
                .SetProperty(r => r.Error, message), ct);
        return await NotifyChangedAsync(changed, r => ids.Contains(r.Id) && r.Status == ExternalReviewStatus.Unavailable, ct);
    }

    /// <summary>Credits the batch's reviewed items to <paramref name="reviewer"/>, and to <paramref name="model"/> when known.</summary>
    public async Task<int> SetBatchReviewerAsync(Guid batchId, string reviewer, string? model, CancellationToken ct)
    {
        model = string.IsNullOrWhiteSpace(model) ? null : Truncate(model.Trim(), MaxModelLength);
        var changed = await db.ExternalReviews.Where(r => r.BatchId == batchId && r.Status == ExternalReviewStatus.Reviewed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Reviewer, reviewer)
                .SetProperty(r => r.ReviewerModel, r => model ?? r.ReviewerModel), ct);
        return await NotifyChangedAsync(changed, r => r.BatchId == batchId && r.Status == ExternalReviewStatus.Reviewed, ct);
    }

    /// <summary>The outcome the target shows now (what Claude reviewed); null when nothing is pending.</summary>
    private async Task<GroupOutcome?> ShownOutcomeAsync(ExternalReviewRow row, CancellationToken ct) =>
        row.TargetType == ExternalReviewTarget.Group
            ? await reviewQuery.PendingOutcomeAsync(row.SenderAddress, row.GroupKey!, ct)
            : await db.Suggestions.AsNoTracking()
                .Where(s => s.Id == row.SuggestionId && s.Status == SuggestionStatus.Pending)
                .Select(s => new GroupOutcome(s.TopicLabel, s.NeedsAction, s.ToBeDeleted))
                .SingleOrDefaultAsync(ct);

    /// <summary>
    /// Decides the target's pending suggestions as the verdict says, inside the caller's transaction; anything but
    /// <see cref="ExternalReviewResult.Ok"/> leaves it to roll back.
    /// </summary>
    private async Task<ExternalReviewResult> DecideAsync(ExternalReviewRow row, CancellationToken ct)
    {
        if (row.Status != ExternalReviewStatus.Reviewed || row.Resolution != ExternalReviewResolution.None || row.Verdict is null)
        {
            return ExternalReviewResult.Conflict;
        }

        if (row.Verdict == ReviewVerdict.NeedsHuman)
        {
            return ExternalReviewResult.NeedsHuman;
        }

        var alternative = row.Verdict == ReviewVerdict.Alternative;
        if (alternative && !ReviewVerdictValidation.IsValidLabel(row.VerdictTopicLabel))
        {
            return ExternalReviewResult.InvalidVerdict;
        }

        var outcome = row.VerdictTopicLabel is { } label
            ? new GroupOutcome(label, row.VerdictNeedsAction ?? false, row.VerdictToBeDeleted ?? false)
            : null;
        GroupDecisionResponse response;
        if (row.TargetType == ExternalReviewTarget.Suggestion)
        {
            response = await review.ApprovePendingAsync(row.SuggestionId!.Value, alternative ? outcome : null, ct);
        }
        else if (outcome is null)
        {
            // Nothing was pending when Claude agreed.
            return ExternalReviewResult.AlreadyDecided;
        }
        else
        {
            response = alternative
                ? await review.EditGroupAsync(row.SenderAddress, row.GroupKey!, outcome, ct)
                : await review.DecideGroupAsync(row.SenderAddress, row.GroupKey!, DecisionOutcome.Approved, outcome, ct);
        }

        return response.Changed > 0 ? ExternalReviewResult.Ok
            : response.Skipped.Count > 0 ? ExternalReviewResult.NotApplicable
            : ExternalReviewResult.AlreadyDecided;
    }

    /// <summary>A conditional update: Conflict when the row exists but <paramref name="allowed"/> does not hold.</summary>
    private async Task<(ExternalReviewResult Result, ExternalReviewDto? Item)> UpdateAsync(
        Guid id,
        Expression<Func<ExternalReviewRow, bool>> allowed,
        Action<UpdateSettersBuilder<ExternalReviewRow>> set,
        CancellationToken ct)
    {
        int changed;
        try
        {
            changed = await db.ExternalReviews.Where(r => r.Id == id).Where(allowed).ExecuteUpdateAsync(set, ct);
        }
        catch (Exception ex) when (IsOpenItemConflict(ex))
        {
            changed = 0;
        }

        var item = await query.GetAsync(id, ct);
        if (item is null)
        {
            return (ExternalReviewResult.NotFound, null);
        }

        if (changed == 1)
        {
            await notifier.NotifyAsync(item, ct);
        }

        return (changed == 1 ? ExternalReviewResult.Ok : ExternalReviewResult.Conflict, item);
    }

    private async Task NotifyAsync(IEnumerable<ExternalReviewDto> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            await notifier.NotifyAsync(item, ct);
        }
    }

    /// <summary>Publishes the rows matching <paramref name="which"/> when <paramref name="changed"/> is positive; returns it.</summary>
    private async Task<int> NotifyChangedAsync(int changed, Expression<Func<ExternalReviewRow, bool>> which, CancellationToken ct)
    {
        if (changed > 0)
        {
            await NotifyAsync(await query.ToDtosAsync(await db.ExternalReviews.AsNoTracking().Where(which).ToListAsync(ct), ct), ct);
        }

        return changed;
    }

    private IQueryable<SuggestionRow> PendingMembers(ExternalReviewRow row) =>
        db.Suggestions.Where(s => s.Status == SuggestionStatus.Pending && (row.TargetType == ExternalReviewTarget.Suggestion
            ? s.Id == row.SuggestionId
            : s.SenderAddress == row.SenderAddress && s.GroupKey == row.GroupKey));

    /// <summary>
    /// Inserts the rows, skipping any whose target already has an open item (the partial unique indexes, so concurrent
    /// creates never give a target two); returns the rows inserted.
    /// </summary>
    private async Task<List<ExternalReviewRow>> InsertWithoutOpenAsync(List<ExternalReviewRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.ConvertAll(r => r.Id).ToArray();
        var types = rows.ConvertAll(r => SnakeCaseEnumConverter<ExternalReviewTarget>.ToDb(r.TargetType)).ToArray();
        var suggestionIds = rows.ConvertAll(r => r.SuggestionId).ToArray();
        var senders = rows.ConvertAll(r => r.SenderAddress).ToArray();
        var keys = rows.ConvertAll(r => r.GroupKey).ToArray();
        var runIds = rows.ConvertAll(r => r.RunId).ToArray();
        var status = SnakeCaseEnumConverter<ExternalReviewStatus>.ToDb(ExternalReviewStatus.Queued);
        var resolution = SnakeCaseEnumConverter<ExternalReviewResolution>.ToDb(ExternalReviewResolution.None);
        var createdAt = rows[0].CreatedAt;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO external_reviews (id, target_type, suggestion_id, sender_address, group_key, run_id, status, resolution, created_at)
            SELECT t.id, t.target_type, t.suggestion_id, t.sender_address, t.group_key, t.run_id, {status}, {resolution}, {createdAt}
            FROM unnest({ids}, {types}, {suggestionIds}, {senders}, {keys}, {runIds})
                AS t(id, target_type, suggestion_id, sender_address, group_key, run_id)
            ON CONFLICT DO NOTHING
            """, ct);
        var inserted = (await db.ExternalReviews.AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct)).ToHashSet();
        return rows.FindAll(r => inserted.Contains(r.Id));
    }

    private static bool IsOpenItemConflict(Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) is
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ExternalReviewRow.OpenSuggestionIndex or ExternalReviewRow.OpenGroupIndex,
        };

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
