using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Senders;

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

/// <summary>
/// Query → chunk → upsert → sender stats. Shared by the mailbox fetch, sender fetches (<c>from:</c> queries) and
/// the incremental fetch (history-touched ids). No LLM involvement.
/// </summary>
public sealed partial class MessageFetchPipeline(
    IGmailClient gmail, MessageUpserter upserter, SenderStatsUpdater senders, AppDbContext db, ILogger<MessageFetchPipeline> logger)
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
    public async Task<int> UpsertByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return 0;
        }

        var metadata = await gmail.GetMessagesMetadataAsync(ids, ct);
        var rows = await upserter.UpsertAsync(metadata, ct);
        await senders.UpdateAsync(rows.Select(r => r.FromAddress), ct);

        // A long fetch shares one context; without this every chunk's SaveChanges re-scans all earlier chunks.
        db.ChangeTracker.Clear();
        return rows.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail rejected the stored page token; listing again from the first page")]
    private static partial void LogPageTokenRejected(ILogger logger, Exception ex);
}
