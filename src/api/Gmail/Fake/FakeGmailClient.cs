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
    private readonly long historyId = FakeMailboxSeed.HistoryId;
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

        throw new ArgumentException("Invalid page token.", nameof(token));
    }
}
