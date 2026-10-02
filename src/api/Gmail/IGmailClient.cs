namespace GmailOrganiser.Gmail;

/// <summary>
/// The Gmail operations the app uses, shaped by its consumers (not a wrapper of the Google SDK).
/// Later milestones add methods as they need them.
/// </summary>
public interface IGmailClient
{
    /// <summary>The connected mailbox's address, message count and current history ID.</summary>
    /// <exception cref="GmailNotConnectedException">No usable refresh token or Google client is configured.</exception>
    Task<GmailProfile> GetProfileAsync(CancellationToken ct);

    /// <summary>One page of message ids matching <paramref name="query"/> (never Spam or Trash).</summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<MessageIdPage> ListMessageIdsAsync(MessageListQuery query, CancellationToken ct);

    /// <summary>
    /// Header metadata for <paramref name="ids"/>, in request order. Ids Gmail no longer knows (deleted meanwhile)
    /// are omitted.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<IReadOnlyList<GmailMessageMetadata>> GetMessagesMetadataAsync(IReadOnlyList<string> ids, CancellationToken ct);
}

/// <param name="HistoryId">Gmail's history ID as a decimal string (it is an unsigned 64-bit number).</param>
public sealed record GmailProfile(string EmailAddress, long MessagesTotal, string HistoryId);

/// <param name="Query">Gmail search syntax, e.g. <c>from:@example.com</c>; null for all mail.</param>
/// <param name="MaxResults">Page size, 1 to <see cref="MaxPageSize"/>.</param>
public sealed record MessageListQuery(string? Query, IReadOnlyList<string>? LabelIds, string? PageToken, int MaxResults)
{
    /// <summary>Gmail's maximum page size for <c>messages.list</c>.</summary>
    public const int MaxPageSize = 500;

    public void EnsureValid()
    {
        if (MaxResults is < 1 or > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxResults), MaxResults, $"MaxResults must be between 1 and {MaxPageSize}.");
        }
    }
}

public sealed record MessageIdPage(IReadOnlyList<MessageRef> Messages, string? NextPageToken, long? ResultSizeEstimate);

public sealed record MessageRef(string Id, string ThreadId);

/// <param name="From">The raw <c>From</c> header; <see cref="GmailMetadataMapper.ParseFrom"/> splits it.</param>
/// <param name="HasAttachment">A heuristic from the top-level MIME type; see <see cref="GmailMetadataMapper"/>.</param>
public sealed record GmailMessageMetadata(
    string Id,
    string ThreadId,
    string HistoryId,
    DateTimeOffset InternalDate,
    IReadOnlyList<string> LabelIds,
    string From,
    string? To,
    string? Subject,
    string? ListId,
    string? ListUnsubscribe,
    string? Snippet,
    int SizeEstimate,
    bool HasAttachment);

/// <summary>The app is not connected to Gmail (or the connection was revoked); the user must reconnect.</summary>
public sealed class GmailNotConnectedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Gmail still rate-limited the request after the last retry; the caller may resume later.</summary>
public sealed class GmailRateLimitedException(string message, Exception? inner = null) : Exception(message, inner);
