using GmailOrganiser.Gmail;
using GmailOrganiser.Senders;

namespace GmailOrganiser.Fetch;

/// <param name="Stored">Messages upserted (ids Gmail no longer knows are skipped).</param>
/// <param name="NextPageToken">Where the next chunk starts; null when the listing has ended.</param>
/// <param name="ResultSizeEstimate">Gmail's estimate for the whole listing, from the last page read.</param>
public sealed record FetchChunkResult(int Stored, string? NextPageToken, long? ResultSizeEstimate);

/// <summary>
/// Query → chunk → upsert → sender stats. Shared by the mailbox fetch, sender fetches (<c>from:</c> queries) and
/// the incremental fetch (history-touched ids). No LLM involvement.
/// </summary>
public sealed class MessageFetchPipeline(IGmailClient gmail, MessageUpserter upserter, SenderStatsUpdater senders)
{
    /// <summary>
    /// Lists pages from <paramref name="query"/>'s page token until <paramref name="chunkSize"/> ids are collected
    /// or the listing ends, then stores them. Pages are sized so a chunk always ends on a page boundary, which makes
    /// the returned token an exact resume point.
    /// </summary>
    /// <exception cref="GmailInvalidPageTokenException">Gmail rejected the starting page token.</exception>
    public async Task<FetchChunkResult> FetchChunkAsync(MessageListQuery query, int chunkSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        var ids = new List<string>(chunkSize);
        var pageToken = query.PageToken;
        long? estimate = null;
        do
        {
            var pageSize = Math.Min(MessageListQuery.MaxPageSize, chunkSize - ids.Count);
            var page = await gmail.ListMessageIdsAsync(query with { PageToken = pageToken, MaxResults = pageSize }, ct);
            ids.AddRange(page.Messages.Select(m => m.Id));
            estimate = page.ResultSizeEstimate ?? estimate;
            pageToken = page.NextPageToken;
        }
        while (pageToken is not null && ids.Count < chunkSize);

        var stored = await UpsertByIdsAsync(ids, ct);
        return new FetchChunkResult(stored, pageToken, estimate);
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
        return rows.Count;
    }
}
