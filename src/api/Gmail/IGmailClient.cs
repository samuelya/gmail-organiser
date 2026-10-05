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

    /// <summary>
    /// The label ids of <paramref name="ids"/> (<c>format=minimal</c>), in request order. Ids Gmail no longer knows
    /// are omitted, as in <see cref="GetMessagesMetadataAsync"/>.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<IReadOnlyList<GmailMessageLabels>> GetMessagesLabelsAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>How many messages carry the label <paramref name="labelId"/> (<c>labels.get</c> <c>messagesTotal</c>).</summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<long> GetLabelMessagesTotalAsync(string labelId, CancellationToken ct);

    /// <summary>
    /// <see cref="GetLabelMessagesTotalAsync"/> for many labels in batch calls, in request order. Labels Gmail no longer
    /// knows (deleted meanwhile) are omitted.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<IReadOnlyList<GmailLabelTotal>> GetLabelsMessagesTotalAsync(IReadOnlyList<string> labelIds, CancellationToken ct);

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

    /// <summary>
    /// The body (as <see cref="GetMessageBodyAsync"/>) and the attachments of <paramref name="id"/> from one
    /// <c>messages.get</c>: named parts, nested multiparts included, in MIME order. Null when Gmail no longer knows the
    /// message. Never store or log the body or inline attachment bytes.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<GmailMessageContent?> GetMessageContentAsync(string id, CancellationToken ct);

    /// <summary>
    /// The decoded bytes of one attachment (<c>messages.attachments.get</c>); null when Gmail no longer knows the message
    /// or attachment. Never store or log the result.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<byte[]?> GetAttachmentContentAsync(string messageId, string attachmentId, CancellationToken ct);

    /// <summary>
    /// The message ids and label ids of thread <paramref name="threadId"/> (<c>threads.get</c>, format=minimal); null
    /// when Gmail no longer knows the thread.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<GmailThreadSummary?> GetThreadSummaryAsync(string threadId, CancellationToken ct);

    /// <summary>Every system and user label of the mailbox.</summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<IReadOnlyList<GmailLabel>> ListLabelsAsync(CancellationToken ct);

    /// <summary>
    /// Creates the user label <paramref name="name"/> (<c>/</c> nests; parents are not created), or returns the existing
    /// label of that name.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is blank, longer than <see cref="GmailLimits.LabelNameMaxLength"/> or reserved by Gmail.
    /// </exception>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<GmailLabel> CreateLabelAsync(string name, CancellationToken ct);

    /// <summary>
    /// Renames the user label <paramref name="id"/> (<c>labels.patch</c>, name only). The id stays, so messages and filters
    /// keep the label; nested labels are separate labels and keep their names.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="newName"/> is not a valid label name.</exception>
    /// <exception cref="GmailLabelExistsException">Another label already has <paramref name="newName"/>.</exception>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<GmailLabel> RenameLabelAsync(string id, string newName, CancellationToken ct);

    /// <summary>
    /// Deletes the user label <paramref name="id"/> (<c>labels.delete</c>; Gmail takes it off every message); a label
    /// Gmail no longer has counts as deleted. Callers check that it is empty first.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task DeleteLabelAsync(string id, CancellationToken ct);

    /// <summary>
    /// Adds and removes labels on up to <see cref="GmailLimits.BatchModifyMaxIds"/> messages in one call; callers chunk.
    /// Idempotent, so a chunk can be re-sent after a lost response. A thin I/O seam: callers write the action (undo) log.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">More than <see cref="GmailLimits.BatchModifyMaxIds"/> ids.</exception>
    /// <exception cref="ArgumentException">Both label lists are empty, or a label is both added and removed.</exception>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task BatchModifyAsync(
        IReadOnlyList<string> ids, IReadOnlyList<string> addLabelIds, IReadOnlyList<string> removeLabelIds, CancellationToken ct);

    /// <summary>Every filter of the account (<c>users.settings.filters.list</c>; Gmail caps an account at 1000).</summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<IReadOnlyList<GmailFilter>> ListFiltersAsync(CancellationToken ct);

    /// <summary>
    /// Creates a filter. Not idempotent: only rate limits are retried, so a lost response can leave a duplicate that the
    /// next filter sync lists. A thin I/O seam: callers write the action (undo) log.
    /// </summary>
    /// <exception cref="ArgumentException">Empty criteria or action, or a forwarding action.</exception>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task<GmailFilter> CreateFilterAsync(GmailFilterCriteria criteria, GmailFilterAction action, CancellationToken ct);

    /// <summary>Deletes filter <paramref name="id"/>; a filter Gmail no longer has counts as deleted.</summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    Task DeleteFilterAsync(string id, CancellationToken ct);
}

/// <summary>Gmail's limits the app's callers must respect.</summary>
public static class GmailLimits
{
    /// <summary>Most message ids <c>messages.batchModify</c> accepts in one call.</summary>
    public const int BatchModifyMaxIds = 1000;

    public const int LabelNameMaxLength = 225;

    /// <summary>Most <c>/</c>-separated levels a label path may have (<see cref="LabelPath.IsValid"/>).</summary>
    public const int LabelMaxSegments = 5;

    /// <summary>Names Gmail refuses for a user label (system label ids and display names), compared case-insensitively.</summary>
    public static readonly IReadOnlySet<string> ReservedLabelNames = new HashSet<string>(
        [
            "INBOX", "UNREAD", "STARRED", "IMPORTANT", "SENT", "DRAFT", "SPAM", "TRASH", "CHAT",
            "CATEGORY_PERSONAL", "CATEGORY_SOCIAL", "CATEGORY_PROMOTIONS", "CATEGORY_UPDATES", "CATEGORY_FORUMS",
            "Drafts", "Sent Mail", "All Mail", "Chats", "Scheduled", "Snoozed",
        ],
        StringComparer.OrdinalIgnoreCase);

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

        if (addLabelIds.Intersect(removeLabelIds, StringComparer.Ordinal).Any())
        {
            throw new ArgumentException("A label cannot be both added and removed.", nameof(removeLabelIds));
        }
    }

    /// <summary>Validates a label name for <see cref="IGmailClient.CreateLabelAsync"/> and <see cref="IGmailClient.RenameLabelAsync"/>.</summary>
    public static void EnsureValidLabelName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > LabelNameMaxLength)
        {
            throw new ArgumentException($"A label name is at most {LabelNameMaxLength} characters.", nameof(name));
        }

        if (ReservedLabelNames.Contains(name.Trim()))
        {
            throw new ArgumentException("Gmail reserves this label name.", nameof(name));
        }
    }
}

/// <summary>An attachment part of a message; the filename can be personal, so log it at Debug only.</summary>
/// <param name="AttachmentId">For <see cref="IGmailClient.GetAttachmentContentAsync"/>; null when Gmail sent the bytes inline.</param>
/// <param name="Size">The decoded size in bytes, as Gmail reports it on the part; null when unknown.</param>
/// <param name="InlineContent">The decoded bytes Gmail sent inline; null with an attachment id, or when they didn't decode.</param>
public sealed record GmailAttachment(string? AttachmentId, string Filename, string MimeType, int? Size, byte[]? InlineContent = null);

/// <summary>A message's body and attachment list, read in one call.</summary>
public sealed record GmailMessageContent(GmailMessageBody Body, IReadOnlyList<GmailAttachment> Attachments);

/// <summary>A thread's messages with their labels, as <see cref="IGmailClient.GetThreadSummaryAsync"/> reads them.</summary>
public sealed record GmailThreadSummary(string Id, IReadOnlyList<GmailThreadMessage> Messages);

public sealed record GmailThreadMessage(string Id, IReadOnlyList<string> LabelIds);

/// <summary>A message's raw text parts; html-to-text conversion happens later.</summary>
public sealed record GmailMessageBody(string? Text, string? Html);

public enum GmailLabelType
{
    System,
    User,
}

public sealed record GmailLabel(string Id, string Name, GmailLabelType Type)
{
    /// <summary>Gmail compares label names case-insensitively; an exact match wins.</summary>
    public static GmailLabel? FindByName(IEnumerable<GmailLabel> labels, string name)
    {
        var list = labels as IReadOnlyList<GmailLabel> ?? [.. labels];
        return list.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal))
            ?? list.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

/// <param name="HistoryId">Gmail's history ID as a decimal string (it is an unsigned 64-bit number).</param>
public sealed record GmailProfile(string EmailAddress, long MessagesTotal, string HistoryId);

/// <param name="Query">Gmail search syntax, e.g. <c>from:@example.com</c>; null for all mail.</param>
/// <param name="MaxResults">Page size, 1 to <see cref="MaxPageSize"/>.</param>
public sealed record MessageListQuery(string? Query, IReadOnlyList<string>? LabelIds, string? PageToken, int MaxResults)
{
    /// <summary>Gmail's maximum page size for <c>messages.list</c>.</summary>
    public const int MaxPageSize = 500;

    /// <summary>Also lists mail in Spam and Trash, which Gmail leaves out by default.</summary>
    public bool IncludeSpamTrash { get; init; }

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
/// <param name="ListUnsubscribePost">The RFC 8058 <c>List-Unsubscribe-Post</c> header; read at unsubscribe time, never stored.</param>
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
    string? ListUnsubscribePost,
    string? Precedence,
    string? AutoSubmitted,
    string? Snippet,
    int SizeEstimate,
    bool HasAttachment);

/// <summary>A message's labels as <c>format=minimal</c> returns them; read state is the <c>UNREAD</c> label.</summary>
public sealed record GmailMessageLabels(string Id, IReadOnlyList<string> LabelIds);

/// <summary>A label's <c>messagesTotal</c> as <c>labels.get</c> returns it.</summary>
public sealed record GmailLabelTotal(string Id, long MessagesTotal);

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

/// <summary>A rename was refused because another label already has the name; nothing changed.</summary>
public sealed class GmailLabelExistsException(string message, Exception? inner = null) : Exception(message, inner);
