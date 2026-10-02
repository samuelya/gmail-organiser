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
    /// <exception cref="GmailInvalidPageTokenException">Gmail rejected <see cref="MessageListQuery.PageToken"/>.</exception>
    Task<MessageIdPage> ListMessageIdsAsync(MessageListQuery query, CancellationToken ct);

    /// <summary>
    /// Header metadata for <paramref name="ids"/>, in request order. Ids Gmail no longer knows (deleted meanwhile)
    /// are omitted.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<IReadOnlyList<GmailMessageMetadata>> GetMessagesMetadataAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>How many messages carry the label <paramref name="labelId"/> (<c>labels.get</c> <c>messagesTotal</c>).</summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<long> GetLabelMessagesTotalAsync(string labelId, CancellationToken ct);

    /// <summary>
    /// One page of <c>history.list</c> records after <paramref name="startHistoryId"/>, with all four history types.
    /// Pass the same start id with every page token of one listing.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    /// <exception cref="ArgumentException"><paramref name="startHistoryId"/> is not a history ID; a data bug, never expiry.</exception>
    /// <exception cref="GmailHistoryExpiredException">Gmail no longer keeps history that old; resync fully.</exception>
    /// <exception cref="GmailInvalidPageTokenException">Gmail rejected <paramref name="pageToken"/>.</exception>
    Task<HistoryPage> ListHistoryAsync(string startHistoryId, string? pageToken, CancellationToken ct);

    /// <summary>
    /// The raw first <c>text/plain</c> and first <c>text/html</c> parts of <paramref name="id"/>, decoded; attachments
    /// and other parts are skipped. Null when Gmail no longer knows the message. Never store or log the result.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<GmailMessageBody?> GetMessageBodyAsync(string id, CancellationToken ct);

    /// <summary>Every system and user label of the mailbox.</summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<IReadOnlyList<GmailLabel>> ListLabelsAsync(CancellationToken ct);

    /// <summary>
    /// Creates the user label <paramref name="name"/> (<c>/</c> nests; parents are not created), or returns the existing
    /// label of that name.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank or longer than <see cref="GmailLimits.LabelNameMaxLength"/>.</exception>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<GmailLabel> CreateLabelAsync(string name, CancellationToken ct);

    /// <summary>
    /// Adds and removes labels on up to <see cref="GmailLimits.BatchModifyMaxIds"/> messages in one call; callers chunk.
    /// Idempotent, so a chunk can be re-sent after a lost response.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">More than <see cref="GmailLimits.BatchModifyMaxIds"/> ids.</exception>
    /// <exception cref="ArgumentException">Both label lists are empty.</exception>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task BatchModifyAsync(
        IReadOnlyList<string> ids, IReadOnlyList<string> addLabelIds, IReadOnlyList<string> removeLabelIds, CancellationToken ct);
}

/// <summary>Gmail's limits the app's callers must respect.</summary>
public static class GmailLimits
{
    /// <summary>Most message ids <c>messages.batchModify</c> accepts in one call.</summary>
    public const int BatchModifyMaxIds = 1000;

    public const int LabelNameMaxLength = 225;

    /// <summary>Validates a <see cref="IGmailClient.BatchModifyAsync"/> request.</summary>
    public static void EnsureValidBatchModify(
        IReadOnlyList<string> ids, IReadOnlyList<string> addLabelIds, IReadOnlyList<string> removeLabelIds)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(addLabelIds);
        ArgumentNullException.ThrowIfNull(removeLabelIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ids.Count, BatchModifyMaxIds, nameof(ids));
        if (addLabelIds.Count == 0 && removeLabelIds.Count == 0)
        {
            throw new ArgumentException("At least one label must be added or removed.", nameof(addLabelIds));
        }
    }

    /// <summary>Validates a label name for <see cref="IGmailClient.CreateLabelAsync"/>.</summary>
    public static void EnsureValidLabelName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > LabelNameMaxLength)
        {
            throw new ArgumentException($"A label name is at most {LabelNameMaxLength} characters.", nameof(name));
        }
    }
}

/// <summary>A message's raw text parts; html-to-text conversion happens later.</summary>
public sealed record GmailMessageBody(string? Text, string? Html);

public enum GmailLabelType
{
    System,
    User,
}

public sealed record GmailLabel(string Id, string Name, GmailLabelType Type);

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

/// <param name="HistoryId">The mailbox's current history ID when the page was read.</param>
public sealed record HistoryPage(IReadOnlyList<HistoryRecord> Records, string? NextPageToken, string HistoryId);

/// <summary>One history record; a message id can appear in several records of one page.</summary>
public sealed record HistoryRecord(
    string Id,
    IReadOnlyList<string> MessagesAdded,
    IReadOnlyList<string> MessagesDeleted,
    IReadOnlyList<LabelChange> LabelsAdded,
    IReadOnlyList<LabelChange> LabelsRemoved);

public sealed record LabelChange(string MessageId, IReadOnlyList<string> LabelIds);

/// <summary>The app is not connected to Gmail (or the connection was revoked); the user must reconnect.</summary>
public sealed class GmailNotConnectedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Gmail still rate-limited the request after the last retry; the caller may resume later.</summary>
public sealed class GmailRateLimitedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Gmail rejected a list page token (expired or malformed); the caller restarts the listing from the first page.</summary>
public sealed class GmailInvalidPageTokenException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Gmail answered 404 to <c>history.list</c>: the start history ID is older than Gmail keeps; resync fully.</summary>
public sealed class GmailHistoryExpiredException(string message, Exception? inner = null) : Exception(message, inner);
