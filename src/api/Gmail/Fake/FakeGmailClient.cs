using System.Globalization;
using System.Net;
using System.Text;
using Google.Apis.Gmail.v1.Data;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Gmail.Fake;

/// <summary>
/// In-memory Gmail mailbox seeded with synthetic <c>example.com</c> data. Used by <c>GMAIL_FAKE=true</c>
/// (the whole app runs without Google) and by tests. Thread-safe; register as a singleton.
/// Like <see cref="GoogleGmailClient"/>, it throws <see cref="GmailNotConnectedException"/> when the
/// <see cref="FakeTokenStore"/> holds no token (e.g. after disconnect), and it retries injected rate limits
/// (<see cref="FailNext"/>) with the same <see cref="GmailRetryPolicy"/>.
/// </summary>
public sealed class FakeGmailClient : IGmailClient
{
    public const string AccountEmail = "user@example.com";
    private const string PageTokenPrefix = "fake-page:";

    private readonly Lock gate = new();
    private readonly FakeTokenStore tokens;
    private readonly GmailRetryPolicy retry;
    private readonly List<FakeMessage> messages;
    private readonly FakeHistory history = new(FakeMailboxSeed.HistoryId);
    private readonly FakeLabelStore labels = new();
    private long historyId = FakeMailboxSeed.HistoryId;
    private int historyPageSize = 100;
    private HttpStatusCode failureStatus;
    private int failuresLeft;

    public FakeGmailClient(FakeTokenStore tokens, TimeProvider time)
        : this(tokens, Seed(time.GetUtcNow()), new GmailRetryPolicy(Options.Create(new GmailOptions()), time))
    {
    }

    public FakeGmailClient(FakeTokenStore tokens, IEnumerable<FakeMessage> messages)
        : this(tokens, messages, new GmailRetryPolicy(Options.Create(new GmailOptions()), TimeProvider.System))
    {
    }

    public FakeGmailClient(FakeTokenStore tokens, IEnumerable<FakeMessage> messages, GmailRetryPolicy retry)
    {
        this.tokens = tokens;
        this.retry = retry;
        this.messages = [.. messages];
    }

    public IReadOnlyList<FakeMessage> Messages
    {
        get
        {
            lock (gate)
            {
                return [.. messages];
            }
        }
    }

    /// <summary>
    /// Makes the next <paramref name="count"/> calls (a list, or one item of a metadata batch) fail with
    /// <paramref name="status"/>. 429 and 403 are rate limits (403 with reason <c>rateLimitExceeded</c>); 401 flags reauth.
    /// </summary>
    public void FailNext(HttpStatusCode status, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (gate)
        {
            failureStatus = status;
            failuresLeft = count;
        }
    }

    /// <summary>Simulates mailbox activity: the profile reports a higher history ID from now on.</summary>
    public void AdvanceHistoryId(long by = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(by);
        lock (gate)
        {
            historyId += by;
        }
    }

    /// <summary>How many history records Gmail keeps; a start id older than the oldest kept record is expired.</summary>
    public int HistoryRetention
    {
        get
        {
            lock (gate)
            {
                return history.Retention;
            }
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            lock (gate)
            {
                history.Retention = value;
                history.Trim();
            }
        }
    }

    /// <summary>Records per <see cref="ListHistoryAsync"/> page.</summary>
    public int HistoryPageSize
    {
        get => Volatile.Read(ref historyPageSize);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            Volatile.Write(ref historyPageSize, value);
        }
    }

    /// <summary>Delivers <paramref name="message"/> (its history id is replaced) and records a <c>messageAdded</c>.</summary>
    public void AddMessage(FakeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (gate)
        {
            if (messages.Exists(m => m.Id == message.Id))
            {
                throw new InvalidOperationException($"Message {message.Id} already exists.");
            }

            var id = NextHistoryId();
            messages.Add(message with { HistoryId = id });
            history.Append(new HistoryRecord(id, [message.Id], [], [], []));
        }
    }

    /// <summary>Deletes <paramref name="id"/> permanently and records a <c>messageDeleted</c>.</summary>
    public void DeleteMessage(string id)
    {
        lock (gate)
        {
            var index = IndexOf(id);
            messages.RemoveAt(index);
            history.Append(new HistoryRecord(NextHistoryId(), [], [id], [], []));
        }
    }

    /// <summary>Replaces the labels of <paramref name="id"/> and records the <c>labelAdded</c>/<c>labelRemoved</c> deltas.</summary>
    public void SetLabels(string id, IReadOnlyList<string> labelIds)
    {
        ArgumentNullException.ThrowIfNull(labelIds);
        lock (gate)
        {
            ReplaceLabels(IndexOf(id), labelIds, recordUnchanged: true);
        }
    }

    private void ReplaceLabels(int index, IReadOnlyList<string> labelIds, bool recordUnchanged)
    {
        var id = messages[index].Id;
        var old = messages[index].LabelIds;
        var added = labelIds.Except(old, StringComparer.Ordinal).ToList();
        var removed = old.Except(labelIds, StringComparer.Ordinal).ToList();
        if (!recordUnchanged && added.Count == 0 && removed.Count == 0)
        {
            return;
        }

        var historyIdText = NextHistoryId();
        messages[index] = messages[index] with { LabelIds = [.. labelIds], HistoryId = historyIdText };
        history.Append(new HistoryRecord(
            historyIdText,
            [],
            [],
            added.Count > 0 ? [new LabelChange(id, added)] : [],
            removed.Count > 0 ? [new LabelChange(id, removed)] : []));
    }

    public async Task<GmailProfile> GetProfileAsync(CancellationToken ct)
    {
        var token = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        lock (gate)
        {
            return new GmailProfile(token.AccountEmail, messages.Count, historyId.ToString(CultureInfo.InvariantCulture));
        }
    }

    public async Task<MessageIdPage> ListMessageIdsAsync(MessageListQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.EnsureValid();
        var matches = FakeGmailQuery.Parse(query.Query);
        var includeSpamTrash = query.LabelIds?.Any(IsSpamOrTrash) == true || FakeGmailQuery.NamesSpamOrTrash(query.Query);
        var offset = DecodePageToken(query.PageToken);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        return await retry.ExecuteAsync(async _ =>
        {
            if (TakeFailure() is { } failure)
            {
                await ThrowFailureAsync(failure).ConfigureAwait(false);
            }

            lock (gate)
            {
                var hits = messages
                    .Where(m => includeSpamTrash || !m.LabelIds.Any(IsSpamOrTrash))
                    .Where(m => query.LabelIds is null || query.LabelIds.All(l => m.LabelIds.Contains(l, StringComparer.OrdinalIgnoreCase)))
                    .Where(matches)
                    .OrderByDescending(m => m.Date)
                    .ThenBy(m => m.Id, StringComparer.Ordinal)
                    .ToList();
                var page = hits.Skip(offset).Take(query.MaxResults).Select(m => new MessageRef(m.Id, m.ThreadId)).ToList();
                var next = offset + page.Count < hits.Count ? EncodePageToken(offset + page.Count) : null;
                return new MessageIdPage(page, next, hits.Count);
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GmailMessageMetadata>> GetMessagesMetadataAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var unique = ids.Distinct(StringComparer.Ordinal).ToList();

        var fetched = await retry.ExecuteBatchAsync<string, GmailMessageMetadata>(unique, async (pending, _) =>
        {
            var succeeded = new List<GmailMessageMetadata>();
            var retryItems = new List<string>();
            foreach (var id in pending)
            {
                if (TakeFailure() is { } failure)
                {
                    if (GmailRetryPolicy.IsRetryable(failure, GmailRetryPolicy.CreateApiException(failure, ReasonFor(failure)).Error))
                    {
                        retryItems.Add(id);
                        continue;
                    }

                    await ThrowFailureAsync(failure).ConfigureAwait(false);
                }

                lock (gate)
                {
                    if (messages.Find(m => m.Id == id) is { } message)
                    {
                        succeeded.Add(GmailMetadataMapper.Map(ToGmailMessage(message)));
                    }
                }
            }

            return new GmailBatchAttempt<string, GmailMessageMetadata>(succeeded, retryItems);
        }, ct).ConfigureAwait(false);

        var byId = fetched.ToDictionary(m => m.Id, StringComparer.Ordinal);
        return [.. unique.Where(byId.ContainsKey).Select(id => byId[id])];
    }

    public async Task<HistoryPage> ListHistoryAsync(string startHistoryId, string? pageToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startHistoryId);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var pageSize = HistoryPageSize;

        return await retry.ExecuteAsync(async _ =>
        {
            if (TakeFailure() is { } failure)
            {
                await ThrowFailureAsync(failure).ConfigureAwait(false);
            }

            lock (gate)
            {
                var (records, next) = history.Page(startHistoryId, pageToken, pageSize);
                return new HistoryPage(records, next, historyId.ToString(CultureInfo.InvariantCulture));
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<long> GetLabelMessagesTotalAsync(string labelId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelId);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        lock (gate)
        {
            return messages.Count(m => m.LabelIds.Contains(labelId, StringComparer.OrdinalIgnoreCase));
        }
    }

    public async Task<GmailMessageBody?> GetMessageBodyAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return await RetryAsync<GmailMessageBody?>(() =>
            messages.Find(m => m.Id == id) is { } m ? new GmailMessageBody(m.BodyText, m.BodyHtml) : null, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GmailLabel>> ListLabelsAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return await RetryAsync(() => labels.All, ct).ConfigureAwait(false);
    }

    public async Task<GmailLabel> CreateLabelAsync(string name, CancellationToken ct)
    {
        GmailLimits.EnsureValidLabelName(name);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return await RetryAsync(() => labels.Create(name), ct).ConfigureAwait(false);
    }

    /// <summary>Updates each known message's labels and records the deltas; unknown ids are skipped, unknown labels are a 400.</summary>
    public async Task BatchModifyAsync(
        IReadOnlyList<string> ids, IReadOnlyList<string> addLabelIds, IReadOnlyList<string> removeLabelIds, CancellationToken ct)
    {
        GmailLimits.EnsureValidBatchModify(ids, addLabelIds, removeLabelIds);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await RetryAsync(() =>
        {
            if (addLabelIds.Concat(removeLabelIds).FirstOrDefault(l => !labels.Exists(l)) is not null)
            {
                throw GmailRetryPolicy.CreateApiException(HttpStatusCode.BadRequest, "invalidArgument");
            }

            foreach (var id in ids.Distinct(StringComparer.Ordinal))
            {
                var index = messages.FindIndex(m => m.Id == id);
                if (index >= 0)
                {
                    var current = messages[index].LabelIds;
                    IReadOnlyList<string> next = [.. current.Except(removeLabelIds, StringComparer.Ordinal)
                        .Concat(addLabelIds.Except(current, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal)];
                    ReplaceLabels(index, next, recordUnchanged: false);
                }
            }

            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="read"/> under the lock, after any injected failure, with the shared retry policy.</summary>
    private Task<T> RetryAsync<T>(Func<T> read, CancellationToken ct) =>
        retry.ExecuteAsync(async _ =>
        {
            if (TakeFailure() is { } failure)
            {
                await ThrowFailureAsync(failure).ConfigureAwait(false);
            }

            lock (gate)
            {
                return read();
            }
        }, ct);

    /// <summary>The seeded synthetic mailbox; see <see cref="FakeMailboxSeed"/>.</summary>
    public static IReadOnlyList<FakeMessage> Seed(DateTimeOffset now) => FakeMailboxSeed.Create(now);

    /// <summary>The <c>format=metadata</c> shape Gmail returns: headers and top-level MIME type, no parts.</summary>
    private static Message ToGmailMessage(FakeMessage m)
    {
        var headers = new List<MessagePartHeader>
        {
            new() { Name = "From", Value = m.From },
            new() { Name = "Subject", Value = m.Subject },
            new() { Name = "Date", Value = m.Date.ToString("r", CultureInfo.InvariantCulture) },
        };
        AddHeader(headers, "To", m.To);
        AddHeader(headers, "List-Id", m.ListId);
        AddHeader(headers, "List-Unsubscribe", m.ListUnsubscribe);

        return new Message
        {
            Id = m.Id,
            ThreadId = m.ThreadId,
            HistoryId = ulong.Parse(m.HistoryId, CultureInfo.InvariantCulture),
            InternalDate = m.Date.ToUnixTimeMilliseconds(),
            LabelIds = [.. m.LabelIds],
            Snippet = m.Snippet,
            SizeEstimate = m.TotalSizeEstimate,
            Payload = new MessagePart { MimeType = m.MimeType, Headers = headers },
        };
    }

    private static void AddHeader(List<MessagePartHeader> headers, string name, string? value)
    {
        if (value is not null)
        {
            headers.Add(new MessagePartHeader { Name = name, Value = value });
        }
    }

    private async Task<OAuthToken> EnsureConnectedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var token = await tokens.GetAsync(ct).ConfigureAwait(false)
            ?? throw new GmailNotConnectedException("The fake Gmail account is not connected.");
        return token.ReauthRequired
            ? throw new GmailNotConnectedException("The fake Gmail connection needs to be reconnected.")
            : token;
    }

    private string NextHistoryId() => (++historyId).ToString(CultureInfo.InvariantCulture);

    private int IndexOf(string id)
    {
        var index = messages.FindIndex(m => m.Id == id);
        return index >= 0 ? index : throw new InvalidOperationException($"Message {id} does not exist.");
    }

    private HttpStatusCode? TakeFailure()
    {
        lock (gate)
        {
            if (failuresLeft == 0)
            {
                return null;
            }

            failuresLeft--;
            return failureStatus;
        }
    }

    /// <summary>Like Gmail with <c>includeSpamTrash=false</c>, listings skip Spam and Trash unless they name them.</summary>
    private static bool IsSpamOrTrash(string labelId) =>
        labelId.Equals("SPAM", StringComparison.OrdinalIgnoreCase) || labelId.Equals("TRASH", StringComparison.OrdinalIgnoreCase);

    private static string? ReasonFor(HttpStatusCode status) => status == HttpStatusCode.Forbidden ? "rateLimitExceeded" : null;

    private async Task ThrowFailureAsync(HttpStatusCode status)
    {
        if (status == HttpStatusCode.Unauthorized)
        {
            await tokens.MarkReauthRequiredAsync(CancellationToken.None).ConfigureAwait(false);
            throw new GmailNotConnectedException("Gmail rejected the stored credentials; reconnect Gmail.");
        }

        throw GmailRetryPolicy.CreateApiException(status, ReasonFor(status));
    }

    private static string EncodePageToken(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(PageTokenPrefix + offset.ToString(CultureInfo.InvariantCulture)));

    private static int DecodePageToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return 0;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            if (text.StartsWith(PageTokenPrefix, StringComparison.Ordinal)
                && int.TryParse(text[PageTokenPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
            {
                return offset;
            }
        }
        catch (FormatException)
        {
        }

        throw new GmailInvalidPageTokenException("Invalid page token.");
    }
}
