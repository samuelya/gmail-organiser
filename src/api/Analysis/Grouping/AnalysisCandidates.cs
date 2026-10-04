using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Analysis.Grouping;

/// <summary>Selects the next messages a run (or its preview) covers, newest first.</summary>
public static class AnalysisCandidates
{
    public const int MaxMessageIds = 500;

    /// <summary>
    /// Not-analysed, not-deleted messages in <paramref name="scope"/>; <see cref="AnalysisScope.Messages"/> takes the
    /// explicit ids that are not analysed, analysed (pending) or rejected (re-analysed, replacing the suggestion), and
    /// skips approved and applied ones. Read-only, untracked.
    /// </summary>
    public static async Task<IReadOnlyList<MessageRow>> QueryAsync(
        AppDbContext db,
        AnalysisScope scope,
        string? senderAddress,
        IReadOnlyCollection<string>? messageIds,
        int count,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var query = db.Messages.AsNoTracking().Where(m => !m.DeletedInGmail);
        query = scope switch
        {
            AnalysisScope.Inbox => NotAnalysed(query).Where(m => m.LabelIds.Contains(MailboxFetchJob.InboxLabelId)),
            AnalysisScope.All => NotAnalysed(query),
            AnalysisScope.Sender => BySender(NotAnalysed(query), senderAddress),
            AnalysisScope.Messages => ExplicitIds(query, messageIds),
            AnalysisScope.Labelled => Labelled(query),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };

        return await query
            .OrderByDescending(m => m.InternalDate)
            .ThenBy(m => m.Id)
            .Take(count)
            .ToListAsync(ct);
    }

    /// <summary>How many messages the labelled scope has left to analyse.</summary>
    public static Task<int> CountLabelledAsync(AppDbContext db, CancellationToken ct) =>
        Labelled(db.Messages.AsNoTracking().Where(m => !m.DeletedInGmail)).CountAsync(ct);

    /// <summary>Explicit ids the messages scope does not analyse; a short inbox simply has fewer candidates.</summary>
    public static int Skipped(AnalysisScope scope, int count, int candidates) =>
        scope == AnalysisScope.Messages ? count - candidates : 0;

    /// <summary>
    /// Whether a run of <paramref name="scope"/> still analyses a frozen candidate: the query's status and deletion
    /// conditions (not the inbox or user labels: a candidate archived, filed or unfiled since the run started is still
    /// analysed).
    /// </summary>
    public static bool IsEligible(AnalysisScope scope, MessageRow m) =>
        !m.DeletedInGmail && (scope == AnalysisScope.Messages
            ? m.AnalysisStatus is AnalysisStatus.NotAnalysed or AnalysisStatus.Analysed or AnalysisStatus.Rejected
            : m.AnalysisStatus == AnalysisStatus.NotAnalysed);

    private static IQueryable<MessageRow> NotAnalysed(IQueryable<MessageRow> query) =>
        query.Where(m => m.AnalysisStatus == AnalysisStatus.NotAnalysed);

    // Npgsql runs this as EXISTS over unnest(label_ids); the constant keeps it a LIKE 'Label\_%' prefix match.
    private static IQueryable<MessageRow> Labelled(IQueryable<MessageRow> query) =>
        NotAnalysed(query).Where(m => m.LabelIds.Any(l => l.StartsWith(GmailLabelIds.UserPrefix)));

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
