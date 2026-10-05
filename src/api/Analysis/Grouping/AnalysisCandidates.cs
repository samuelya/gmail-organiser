using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Policies;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Analysis.Grouping;

/// <summary>Selects the next messages a run (or its preview) covers, newest first.</summary>
public static class AnalysisCandidates
{
    public const int MaxMessageIds = 500;

    /// <summary>
    /// Not-analysed, not-deleted messages in <paramref name="scope"/>; <see cref="AnalysisScope.Messages"/> takes the
    /// explicit ids that are not analysed, analysed (pending) or rejected (re-analysed, replacing the suggestion), and
    /// skips approved and applied ones. <see cref="AnalysisScope.Labelled"/> takes mail with a personal label (the app's
    /// action and delete labels in <paramref name="appLabelIds"/> do not count). The inbox, all and labelled scopes leave
    /// out mail an approved single-label sender policy decides (#360); the run's plan drops mixed policies' matches. Read-only, untracked.
    /// </summary>
    public static async Task<IReadOnlyList<MessageRow>> QueryAsync(
        AppDbContext db,
        AnalysisScope scope,
        string? senderAddress,
        IReadOnlyCollection<string>? messageIds,
        int count,
        IReadOnlyList<string> appLabelIds,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var query = db.Messages.AsNoTracking().Where(m => !m.DeletedInGmail);
        query = scope switch
        {
            AnalysisScope.Inbox => Uncovered(db, NotAnalysed(query).Where(m => m.LabelIds.Contains(MailboxFetchJob.InboxLabelId))),
            AnalysisScope.All => Uncovered(db, NotAnalysed(query)),
            AnalysisScope.Sender => BySender(NotAnalysed(query), senderAddress),
            AnalysisScope.Messages => ExplicitIds(query, messageIds),
            AnalysisScope.Labelled => Uncovered(db, Labelled(query, appLabelIds)),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };

        return await query
            .OrderByDescending(m => m.InternalDate)
            .ThenBy(m => m.Id)
            .Take(count)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Not-deleted messages per analysis status, and how many of them the labelled scope has left to analyse, in one
    /// query.
    /// </summary>
    public static async Task<(IReadOnlyDictionary<AnalysisStatus, int> ByStatus, int Labelled)> CountAsync(
        AppDbContext db, IReadOnlyList<string> appLabelIds, CancellationToken ct)
    {
        var app = appLabelIds.ToArray();
        var rows = await db.Messages.AsNoTracking()
            .Where(m => !m.DeletedInGmail)
            .GroupBy(m => m.AnalysisStatus)
            .Select(g => new
            {
                Status = g.Key,
                Count = g.Count(),
                Labelled = g.Count(m => m.LabelIds.Any(l => l.StartsWith(GmailLabelIds.UserPrefix) && !app.Contains(l))),
            })
            .ToListAsync(ct);
        return (rows.ToDictionary(r => r.Status, r => r.Count),
            rows.Where(r => r.Status == AnalysisStatus.NotAnalysed).Sum(r => r.Labelled));
    }

    /// <summary>Explicit ids the messages scope does not analyse; a short inbox simply has fewer candidates.</summary>
    public static int Skipped(AnalysisScope scope, int count, int candidates) =>
        scope == AnalysisScope.Messages ? count - candidates : 0;

    /// <summary>
    /// Whether a run of <paramref name="scope"/> still analyses a frozen candidate: the query's status and deletion
    /// conditions, and for the labelled scope a personal label (a candidate unfiled since the run started is skipped).
    /// The inbox is not rechecked: a candidate archived since the run started is still analysed.
    /// </summary>
    public static bool IsEligible(AnalysisScope scope, MessageRow m, PersonalLabels labels) =>
        !m.DeletedInGmail && scope switch
        {
            AnalysisScope.Messages => m.AnalysisStatus is AnalysisStatus.NotAnalysed or AnalysisStatus.Analysed or AnalysisStatus.Rejected,
            AnalysisScope.Labelled => m.AnalysisStatus == AnalysisStatus.NotAnalysed && m.LabelIds.Any(labels.IsPersonal),
            _ => m.AnalysisStatus == AnalysisStatus.NotAnalysed,
        };

    /// <summary>
    /// Whether a compare run still covers a frozen candidate: whatever its analysis status, as long as it is not deleted
    /// in Gmail and its frozen suggestion still exists (<paramref name="withSuggestion"/>).
    /// </summary>
    public static bool IsCompareEligible(MessageRow m, IReadOnlySet<string> withSuggestion) =>
        !m.DeletedInGmail && withSuggestion.Contains(m.Id);

    private static IQueryable<MessageRow> Uncovered(AppDbContext db, IQueryable<MessageRow> query) =>
        PolicyCoverage.WithoutPolicySuggestion(db, query);

    private static IQueryable<MessageRow> NotAnalysed(IQueryable<MessageRow> query) =>
        query.Where(m => m.AnalysisStatus == AnalysisStatus.NotAnalysed);

    // Npgsql runs this as EXISTS over unnest(label_ids); the constant keeps it a LIKE 'Label\_%' prefix match.
    // CountAsync repeats the predicate inside its aggregate.
    private static IQueryable<MessageRow> Labelled(IQueryable<MessageRow> query, IReadOnlyList<string> appLabelIds)
    {
        var app = appLabelIds.ToArray();
        return NotAnalysed(query).Where(m => m.LabelIds.Any(l => l.StartsWith(GmailLabelIds.UserPrefix) && !app.Contains(l)));
    }

    private static IQueryable<MessageRow> BySender(IQueryable<MessageRow> query, string? senderAddress)
    {
        // Validated into a local: a throw inside the expression would surface as an EF translation error.
        if (string.IsNullOrWhiteSpace(senderAddress))
        {
            throw new ArgumentException("A sender scope needs a sender address.", nameof(senderAddress));
        }

        var address = senderAddress.Trim().ToLowerInvariant();
        return query.Where(m => m.FromAddress == address);
    }

    private static IQueryable<MessageRow> ExplicitIds(IQueryable<MessageRow> query, IReadOnlyCollection<string>? messageIds)
    {
        if (messageIds is null || messageIds.Count is 0 or > MaxMessageIds)
        {
            throw new ArgumentException($"A messages scope needs 1 to {MaxMessageIds} message ids.", nameof(messageIds));
        }

        var ids = messageIds.Distinct(StringComparer.Ordinal).ToArray();
        return query.Where(m => ids.Contains(m.Id)
            && (m.AnalysisStatus == AnalysisStatus.NotAnalysed
                || m.AnalysisStatus == AnalysisStatus.Analysed
                || m.AnalysisStatus == AnalysisStatus.Rejected));
    }
}
