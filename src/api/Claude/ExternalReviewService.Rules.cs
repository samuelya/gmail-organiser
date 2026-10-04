using System.Linq.Expressions;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Rules.Labels;
using GmailOrganiser.Rules.Review;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GmailOrganiser.Claude;

/// <summary>The target checks shared by every item type, and the M6 targets: label plans and filter findings.</summary>
public sealed partial class ExternalReviewService
{
    /// <summary>
    /// Whether the item's target still waits for a decision: a pending suggestion (or group member), a <c>draft</c>
    /// plan, an <c>open</c> finding. A verdict on a decided target is refused and its item cancelled.
    /// </summary>
    public static Expression<Func<ExternalReviewRow, bool>> TargetOpen(AppDbContext db) => r =>
        (r.TargetType == ExternalReviewTarget.LabelPlan
            && db.LabelPlans.Any(p => p.Id == r.LabelPlanId && p.Status == LabelPlanStatus.Draft))
        || (r.TargetType == ExternalReviewTarget.FilterFinding
            && db.FilterFindings.Any(f => f.Id == r.FilterFindingId && f.Status == FilterFindingStatus.Open))
        || ((r.TargetType == ExternalReviewTarget.Suggestion || r.TargetType == ExternalReviewTarget.Group)
            && db.Suggestions.Any(s => s.Status == SuggestionStatus.Pending
                && (r.TargetType == ExternalReviewTarget.Suggestion
                    ? s.Id == r.SuggestionId
                    : s.SenderAddress == r.SenderAddress && s.GroupKey == r.GroupKey)));

    /// <summary>
    /// Stores Claude's taxonomy feedback on the plan's open item: <c>alternative</c> with a non-empty
    /// <paramref name="structure"/>, else <c>agree</c> (an empty array is no structure); <paramref name="comments"/> is the reasoning. Returns the item id
    /// when there is one.
    /// </summary>
    public async Task<(ReviewVerdictResult Result, string? Reason, Guid? ItemId)> SubmitTaxonomyFeedbackAsync(
        Guid planId, string comments, IReadOnlyList<string>? structure, string reviewer, string? model, CancellationToken ct)
    {
        var id = await db.ExternalReviews.AsNoTracking()
            .Where(r => r.TargetType == ExternalReviewTarget.LabelPlan && r.LabelPlanId == planId)
            .Where(r => r.Status == ExternalReviewStatus.Queued || r.Status == ExternalReviewStatus.Running
                || (r.Status == ExternalReviewStatus.Reviewed && r.Resolution == ExternalReviewResolution.None))
            .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        if (id is null)
        {
            return (ReviewVerdictResult.NotFound, "This plan has no open review item; the user sends a plan to Claude from the Rules page.", null);
        }

        var alternative = structure is { Count: > 0 } ? structure : null;
        var verdict = new ReviewVerdictInput(
            alternative is null ? ReviewVerdict.Agree : ReviewVerdict.Alternative, null, null, null, null, comments, reviewer, model,
            AlternativeStructure: alternative);
        var (result, reason) = await SubmitVerdictAsync(id.Value, verdict, ct);
        return (result, reason, id);
    }

    /// <summary>
    /// The plan and finding items to queue: <see cref="CreateExternalReviewsResult.TargetNotFound"/> for an unknown
    /// one, <see cref="CreateExternalReviewsResult.TargetConflict"/> for a plan that is not a draft, a finding that is
    /// not open, or one that already has an open item.
    /// </summary>
    private async Task<(CreateExternalReviewsResult Result, List<ExternalReviewRow> Rows)> RulesCandidatesAsync(
        Guid? labelPlanId, Guid[] findingIds, DateTimeOffset now, CancellationToken ct)
    {
        var rows = new List<ExternalReviewRow>();
        if (labelPlanId is { } planId)
        {
            var status = await db.LabelPlans.AsNoTracking().Where(p => p.Id == planId)
                .Select(p => (LabelPlanStatus?)p.Status).SingleOrDefaultAsync(ct);
            if (status is null)
            {
                return (CreateExternalReviewsResult.TargetNotFound, []);
            }

            if (status != LabelPlanStatus.Draft)
            {
                return (CreateExternalReviewsResult.TargetConflict, []);
            }

            var row = New(ExternalReviewTarget.LabelPlan, "", null, null, null, now);
            row.LabelPlanId = planId;
            rows.Add(row);
        }

        var findings = findingIds.Distinct().ToList();
        if (findings.Count > 0)
        {
            var statuses = await db.FilterFindings.AsNoTracking().Where(f => findings.Contains(f.Id))
                .ToDictionaryAsync(f => f.Id, f => f.Status, ct);
            if (statuses.Count < findings.Count)
            {
                return (CreateExternalReviewsResult.TargetNotFound, []);
            }

            if (statuses.Values.Any(s => s != FilterFindingStatus.Open))
            {
                return (CreateExternalReviewsResult.TargetConflict, []);
            }

            foreach (var findingId in findings)
            {
                var row = New(ExternalReviewTarget.FilterFinding, "", null, null, null, now);
                row.FilterFindingId = findingId;
                rows.Add(row);
            }
        }

        var hasOpen = rows.Count > 0 && await db.ExternalReviews.AnyAsync(r =>
            (r.Status == ExternalReviewStatus.Queued || r.Status == ExternalReviewStatus.Running
                || (r.Status == ExternalReviewStatus.Reviewed && r.Resolution == ExternalReviewResolution.None))
            && ((labelPlanId != null && r.LabelPlanId == labelPlanId) || (r.FilterFindingId != null && findings.Contains(r.FilterFindingId.Value))), ct);
        return hasOpen ? (CreateExternalReviewsResult.TargetConflict, []) : (CreateExternalReviewsResult.Ok, rows);
    }

    /// <summary>
    /// <see cref="SubmitVerdictAsync"/> for a plan or finding item: the verdict is stored as given (nothing is computed
    /// from the target), and only while the plan is a draft or the finding open.
    /// </summary>
    private async Task<(ReviewVerdictResult Result, string? Reason)> SubmitRulesVerdictAsync(
        Guid id, ExternalReviewTarget target, ReviewVerdictInput verdict, CancellationToken ct)
    {
        if (ReviewVerdictValidation.ValidateRules(verdict, target, out var structure) is { } invalid)
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

        if (await CloseIfDecidedAsync(row, tx, ct))
        {
            return (ReviewVerdictResult.AlreadyDecided, null);
        }

        row.Status = ExternalReviewStatus.Reviewed;
        row.Verdict = verdict.Verdict;
        row.VerdictFilterCriteria = verdict.Verdict == ReviewVerdict.Alternative && target == ExternalReviewTarget.FilterFinding
            ? verdict.FilterCriteria
            : null;
        row.AlternativeStructure = structure;
        row.Reasoning = Truncate(verdict.Reasoning.Trim(), MaxReasoningLength);
        row.Reviewer = verdict.Reviewer;
        row.ReviewerModel = string.IsNullOrWhiteSpace(verdict.Model) ? null : verdict.Model.Trim();
        row.ReviewedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await NotifyAsync(await query.ToDtosAsync([row], ct), ct);
        return (ReviewVerdictResult.Ok, null);
    }

    /// <summary>
    /// When the user decided the locked <paramref name="row"/>'s target meanwhile, nobody waits for its verdict: the
    /// item is cancelled (committed and published) rather than left open, and true is returned.
    /// </summary>
    private async Task<bool> CloseIfDecidedAsync(ExternalReviewRow row, IDbContextTransaction tx, CancellationToken ct)
    {
        if (await TargetOpenAsync(row.Id, ct))
        {
            return false;
        }

        row.Status = ExternalReviewStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await NotifyAsync(await query.ToDtosAsync([row], ct), ct);
        return true;
    }

    private Task<bool> TargetOpenAsync(Guid id, CancellationToken ct) =>
        db.ExternalReviews.AsNoTracking().Where(r => r.Id == id).Where(TargetOpen(db)).AnyAsync(ct);
}
