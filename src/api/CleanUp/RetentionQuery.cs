using System.Linq.Expressions;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Policies;
using GmailOrganiser.Settings;

namespace GmailOrganiser.CleanUp;

/// <summary>
/// The retention candidates in SQL (#368): live applied messages, not in Trash and not delete-labelled, past their
/// effective retention (the suggestion's approved sub-rule's <c>RetentionDays</c>, else its approved policy's, else the
/// mail type's days), unless that rule or policy keeps. Protection, the transactional guard and the policy matching the
/// sender now are checked by <see cref="RetentionSweepJob.PlanAsync"/>, not here, so a count is an upper bound.
/// </summary>
public static class RetentionQuery
{
    public static IQueryable<MessageRow> Expired(AppDbContext db, AppSettings settings, string? deleteLabelId, DateTimeOffset now)
    {
        var newest = now.AddDays(-RetentionSettings.MinDays);
        var messages = db.Messages.Where(m => m.AnalysisStatus == AnalysisStatus.Applied && !m.DeletedInGmail
            && !m.LabelIds.Contains(CleanUpQuery.TrashLabel) && m.InternalDate < newest);
        if (deleteLabelId is not null)
        {
            messages = messages.Where(m => !m.LabelIds.Contains(deleteLabelId));
        }

        var candidates =
            from m in messages
            join s in db.Suggestions on m.Id equals s.MessageId
            let rule = db.SenderPolicyRules.FirstOrDefault(r => r.Id == s.PolicyRuleId && r.Status == PolicyStatus.Approved)
            let policy = db.SenderPolicies.FirstOrDefault(p => p.Id == s.PolicyId && p.Status == PolicyStatus.Approved)
            where rule != null ? rule.Action != PolicyAction.Keep : policy == null || policy.Action != PolicyAction.Keep
            select new Candidate
            {
                Id = m.Id,
                InternalDate = m.InternalDate,
                MailType = s.MailType,
                OverrideDays = rule != null && rule.RetentionDays != null ? rule.RetentionDays
                    : policy != null ? policy.RetentionDays : null,
            };
        var ids = candidates.Where(PastRetention(settings.Retention, now)).Select(c => c.Id);
        return db.Messages.Where(m => ids.Contains(m.Id));
    }

    /// <summary>
    /// <c>c.OverrideDays != null ? c.InternalDate &lt; now − c.OverrideDays : (c.MailType == T1 &amp;&amp; c.InternalDate &lt; cutoff1) || …</c>,
    /// one term per mail type with days; a type that keeps has no term.
    /// </summary>
    private static Expression<Func<Candidate, bool>> PastRetention(RetentionSettings retention, DateTimeOffset now)
    {
        var c = Expression.Parameter(typeof(Candidate), "c");
        var date = Expression.Property(c, nameof(Candidate.InternalDate));
        var type = Expression.Property(c, nameof(Candidate.MailType));
        var days = Expression.Property(c, nameof(Candidate.OverrideDays));
        var addDays = typeof(DateTimeOffset).GetMethod(nameof(DateTimeOffset.AddDays), [typeof(double)])!;
        var overrideCutoff = Expression.Call(
            Expression.Constant(now), addDays, Expression.Negate(Expression.Convert(days, typeof(double))));
        Expression byType = Expression.Constant(false);
        foreach (var t in Enum.GetValues<MailType>())
        {
            if (retention.DaysFor(t) is { } d)
            {
                byType = Expression.OrElse(byType, Expression.AndAlso(
                    Expression.Equal(type, Expression.Constant(t, typeof(MailType?))),
                    Expression.LessThan(date, Expression.Constant(now.AddDays(-d)))));
            }
        }

        var body = Expression.Condition(
            Expression.NotEqual(days, Expression.Constant(null, typeof(int?))),
            Expression.LessThan(date, overrideCutoff),
            byType);
        return Expression.Lambda<Func<Candidate, bool>>(body, c);
    }

    private sealed class Candidate
    {
        public string Id { get; init; } = "";
        public DateTimeOffset InternalDate { get; init; }
        public MailType? MailType { get; init; }
        public int? OverrideDays { get; init; }
    }
}
