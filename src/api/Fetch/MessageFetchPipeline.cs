using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Senders;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Fetch;

/// <param name="Stored">Messages upserted (ids Gmail no longer knows are skipped).</param>
/// <param name="NextPageToken">Where the next chunk starts; null when the listing has ended.</param>
/// <param name="ResultSizeEstimate">Gmail's estimate for the whole listing, from the chunk's first page.</param>
/// <param name="Restarted">Gmail rejected the caller's page token, so the chunk was listed from the first page.</param>
public sealed record FetchChunkResult(int Stored, string? NextPageToken, long? ResultSizeEstimate, bool Restarted = false);

/// <param name="Ids">The ids listed, in Gmail's order.</param>
/// <param name="NextPageToken">Where the next chunk starts; null when the listing has ended.</param>
/// <param name="ResultSizeEstimate">Gmail's estimate for the whole listing, from the chunk's first page.</param>
/// <param name="Restarted">Gmail rejected the caller's page token, so the chunk was listed from the first page.</param>
public sealed record ListedChunk(IReadOnlyList<string> Ids, string? NextPageToken, long? ResultSizeEstimate, bool Restarted = false);

/// <param name="Stored">Messages upserted (for a labels-only refresh: stored rows whose labels were re-read).</param>
/// <param name="Deleted">Stored messages newly marked <c>deleted_in_gmail</c>.</param>
public sealed record RefreshResult(int Stored, int Deleted);

/// <summary>
/// Query → chunk → upsert → sender stats. Shared by the mailbox fetch, sender fetches (<c>from:</c> queries) and
/// the incremental fetch (history-touched ids). No LLM involvement.
/// </summary>
public sealed partial class MessageFetchPipeline(
    IGmailClient gmail, MessageUpserter upserter, SenderStatsUpdater senders, AppDbContext db, TimeProvider time,
    ILogger<MessageFetchPipeline> logger)
{
    private const int MaxChunkAttempts = 2;

    /// <summary>Lists one chunk (see <see cref="ListChunkAsync"/>) and stores it.</summary>
    public async Task<FetchChunkResult> FetchChunkAsync(MessageListQuery query, int chunkSize, CancellationToken ct)
    {
        var chunk = await ListChunkAsync(query, chunkSize, ct);
        var stored = await UpsertByIdsAsync(chunk.Ids, ct);
        return new FetchChunkResult(stored, chunk.NextPageToken, chunk.ResultSizeEstimate, chunk.Restarted);
    }

    /// <summary>
    /// Lists pages from <paramref name="query"/>'s page token until <paramref name="chunkSize"/> ids are collected
    /// or the listing ends. Pages are sized so a chunk always ends on a page boundary, which makes the returned token
    /// an exact resume point. When Gmail rejects the caller's stored token (expired), the chunk is listed from the
    /// first page instead and <see cref="ListedChunk.Restarted"/> is set: the upsert makes re-reading safe, and the
    /// caller only resets its own counters.
    /// </summary>
    public async Task<ListedChunk> ListChunkAsync(MessageListQuery query, int chunkSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        try
        {
            return await ListFromAsync(query, chunkSize, ct);
        }
        catch (GmailInvalidPageTokenException ex) when (query.PageToken is not null)
        {
            LogPageTokenRejected(logger, ex);
            return await ListFromAsync(query with { PageToken = null }, chunkSize, ct) with { Restarted = true };
        }
    }

    /// <exception cref="GmailInvalidPageTokenException">Gmail rejected <paramref name="query"/>'s page token.</exception>
    private async Task<ListedChunk> ListFromAsync(MessageListQuery query, int chunkSize, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var ids = new List<string>(chunkSize);
            var pageToken = query.PageToken;
            long? estimate = null;
            try
            {
                do
                {
                    var pageSize = Math.Min(MessageListQuery.MaxPageSize, chunkSize - ids.Count);
                    var page = await gmail.ListMessageIdsAsync(query with { PageToken = pageToken, MaxResults = pageSize }, ct);
                    ids.AddRange(page.Messages.Select(m => m.Id));
                    estimate ??= page.ResultSizeEstimate;
                    pageToken = page.NextPageToken;
                }
                while (pageToken is not null && ids.Count < chunkSize);

                return new ListedChunk(ids, pageToken, estimate);
            }
            catch (GmailInvalidPageTokenException ex) when (!string.Equals(pageToken, query.PageToken, StringComparison.Ordinal))
            {
                // Not the caller's token, so not the caller's problem: only this chunk's listing starts over.
                if (attempt == MaxChunkAttempts)
                {
                    throw new InvalidOperationException("Gmail kept rejecting a page token it had just issued.", ex);
                }
            }
        }
    }

    /// <summary>Fetches metadata for <paramref name="ids"/>, upserts it and refreshes the affected senders.</summary>
    public async Task<int> UpsertByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
        (await StoreAsync(ids, refresh: false, ct)).Stored;

    /// <summary>
    /// Re-reads <paramref name="ids"/> and upserts them, which overwrites their labels. Ids Gmail no longer knows are
    /// marked deleted; ids not stored yet that are in Spam or Trash are skipped, as the mailbox fetch never lists them.
    /// </summary>
    public Task<RefreshResult> RefreshByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
        StoreAsync(ids, refresh: true, ct);

    /// <summary>
    /// Re-reads only the labels of <paramref name="ids"/> (<c>format=minimal</c>) and writes <c>label_ids</c>, the
    /// derived category and <c>deleted_in_gmail</c> of their stored rows; every other column is untouched and no row
    /// is inserted. Ids Gmail no longer knows are marked deleted. Senders are refreshed for rows whose deleted state changed.
    /// </summary>
    public async Task<RefreshResult> RefreshLabelsByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var unique = ids.Distinct(StringComparer.Ordinal).ToList();
        if (unique.Count == 0)
        {
            return new RefreshResult(0, 0);
        }

        var labels = await gmail.GetMessagesLabelsAsync(unique, ct);
        var deleted = await MarkDeletedCoreAsync([.. unique.Except(labels.Select(l => l.Id), StringComparer.Ordinal)], ct);
        var byId = labels.ToDictionary(l => l.Id, StringComparer.Ordinal);
        var live = labels.Select(l => l.Id).ToList();
        var rows = await db.Messages.Where(m => live.Contains(m.Id)).ToListAsync(ct);
        var now = time.GetUtcNow();
        List<string> changed = [];
        foreach (var row in rows)
        {
            var labelIds = byId[row.Id].LabelIds;
            // The same TRASH rule as MessageUpserter: in Trash is not live, out of Trash is live again.
            var inTrash = labelIds.Contains(MailboxFetchJob.TrashLabelId, StringComparer.OrdinalIgnoreCase);
            if (inTrash != row.DeletedInGmail)
            {
                changed.Add(row.FromAddress);
            }

            var entry = db.Entry(row);
            row.LabelIds = [.. labelIds];
            row.Category = MessageUpserter.CategoryOf(labelIds);
            row.DeletedInGmail = inTrash;
            entry.DetectChanges();
            if (entry.State == EntityState.Modified)
            {
                row.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
        await senders.UpdateAsync(changed.Concat(deleted), ct);
        db.ChangeTracker.Clear();
        return new RefreshResult(rows.Count, deleted.Count);
    }

    private async Task<RefreshResult> StoreAsync(IReadOnlyList<string> ids, bool refresh, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var unique = ids.Distinct(StringComparer.Ordinal).ToList();
        if (unique.Count == 0)
        {
            return new RefreshResult(0, 0);
        }

        var metadata = await gmail.GetMessagesMetadataAsync(unique, ct);
        List<string> deleted = [];
        if (refresh)
        {
            var known = await db.Messages.Where(m => unique.Contains(m.Id)).Select(m => m.Id).ToHashSetAsync(StringComparer.Ordinal, ct);
            deleted = await MarkDeletedCoreAsync([.. unique.Except(metadata.Select(m => m.Id), StringComparer.Ordinal)], ct);
            metadata = [.. metadata.Where(m => known.Contains(m.Id) || !m.LabelIds.Any(IsSpamOrTrash))];
        }

        var rows = await upserter.UpsertAsync(metadata, ct);
        await senders.UpdateAsync(rows.Select(r => r.FromAddress).Concat(deleted), ct);

        // A long fetch shares one context; without this every chunk's SaveChanges re-scans all earlier chunks.
        db.ChangeTracker.Clear();
        return new RefreshResult(rows.Count, deleted.Count);
    }

    /// <summary>Sets <c>deleted_in_gmail</c> on the stored rows of <paramref name="ids"/> (rows are never removed).</summary>
    /// <returns>How many rows were newly marked.</returns>
    public async Task<int> MarkDeletedAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var deleted = await MarkDeletedCoreAsync(ids, ct);
        await senders.UpdateAsync(deleted, ct);
        return deleted.Count;
    }

    /// <returns>The sender address of each row newly marked.</returns>
    private async Task<List<string>> MarkDeletedCoreAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var marked = db.Messages.Where(m => ids.Contains(m.Id) && !m.DeletedInGmail);
        var addresses = await marked.Select(m => m.FromAddress).ToListAsync(ct);
        if (addresses.Count > 0)
        {
            var now = time.GetUtcNow();
            await marked.ExecuteUpdateAsync(set => set.SetProperty(m => m.DeletedInGmail, true).SetProperty(m => m.UpdatedAt, now), ct);
        }

        return addresses;
    }

    private static bool IsSpamOrTrash(string labelId) =>
        labelId.Equals(MailboxFetchJob.SpamLabelId, StringComparison.OrdinalIgnoreCase)
        || labelId.Equals(MailboxFetchJob.TrashLabelId, StringComparison.OrdinalIgnoreCase);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail rejected the stored page token; listing again from the first page")]
    private static partial void LogPageTokenRejected(ILogger logger, Exception ex);
}
