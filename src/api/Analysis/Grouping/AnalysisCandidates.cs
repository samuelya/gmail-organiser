using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Analysis.Grouping;

/// <summary>Selects the next messages a run (or its preview) covers, newest first.</summary>
public static class AnalysisCandidates
{
    public const string InboxLabel = "INBOX";
    public const int MaxMessageIds = 500;

    /// <summary>
    /// Not-analysed, not-deleted messages in <paramref name="scope"/>; <see cref="AnalysisScope.Messages"/> takes the
    /// explicit ids in any status except <see cref="AnalysisStatus.Applied"/> (re-analyse). Read-only, untracked.
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
            AnalysisScope.Inbox => NotAnalysed(query).Where(m => m.LabelIds.Contains(InboxLabel)),
            AnalysisScope.All => NotAnalysed(query),
            AnalysisScope.Sender => NotAnalysed(query).Where(m => m.FromAddress == RequireSender(senderAddress)),
            AnalysisScope.Messages => ExplicitIds(query, messageIds),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };

        return await query
            .OrderByDescending(m => m.InternalDate)
            .ThenBy(m => m.Id)
            .Take(count)
            .ToListAsync(ct);
    }

    private static IQueryable<MessageRow> NotAnalysed(IQueryable<MessageRow> query) =>
        query.Where(m => m.AnalysisStatus == AnalysisStatus.NotAnalysed);

    private static string RequireSender(string? senderAddress) =>
        string.IsNullOrWhiteSpace(senderAddress)
            ? throw new ArgumentException("A sender scope needs a sender address.", nameof(senderAddress))
            : senderAddress.Trim().ToLowerInvariant();

    private static IQueryable<MessageRow> ExplicitIds(IQueryable<MessageRow> query, IReadOnlyCollection<string>? messageIds)
    {
        if (messageIds is null || messageIds.Count is 0 or > MaxMessageIds)
        {
            throw new ArgumentException($"A messages scope needs 1 to {MaxMessageIds} message ids.", nameof(messageIds));
        }

        var ids = messageIds.Distinct(StringComparer.Ordinal).ToArray();
        return query.Where(m => ids.Contains(m.Id) && m.AnalysisStatus != AnalysisStatus.Applied);
    }
}
