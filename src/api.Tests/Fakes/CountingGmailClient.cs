using System.Collections.Concurrent;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>Records every call to the wrapped <see cref="FakeGmailClient"/> and runs a hook after each metadata, labels or history call.</summary>
public sealed class CountingGmailClient(FakeGmailClient inner) : IGmailClient
{
    public FakeGmailClient Inner => inner;

    public ConcurrentQueue<MessageListQuery> ListCalls { get; } = new();

    public ConcurrentQueue<IReadOnlyList<string>> MetadataCalls { get; } = new();

    public ConcurrentQueue<IReadOnlyList<string>> LabelsCalls { get; } = new();

    /// <summary>Runs after the n-th (1-based) metadata call returned.</summary>
    public Func<int, Task>? AfterMetadata { get; set; }

    /// <summary>Rejects the n-th (1-based) list call's page token, as Gmail does for an expired token, when it returns true.</summary>
    public Func<int, MessageListQuery, bool>? RejectPageToken { get; set; }

    private int profileCalls;

    public int ProfileCalls => profileCalls;

    /// <summary>Thrown by every profile call when set, as a failing Gmail would.</summary>
    public Exception? ProfileFailure { get; set; }

    public Task<GmailProfile> GetProfileAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref profileCalls);
        return ProfileFailure is { } failure ? Task.FromException<GmailProfile>(failure) : inner.GetProfileAsync(ct);
    }

    /// <summary>Rewrites the n-th (1-based) list call's page, for example its result size estimate.</summary>
    public Func<int, MessageIdPage, MessageIdPage>? MapPage { get; set; }

    public async Task<MessageIdPage> ListMessageIdsAsync(MessageListQuery query, CancellationToken ct)
    {
        ListCalls.Enqueue(query);
        var call = ListCalls.Count;
        if (RejectPageToken?.Invoke(call, query) == true)
        {
            throw new GmailInvalidPageTokenException("Invalid page token.");
        }

        var page = await inner.ListMessageIdsAsync(query, ct);
        return MapPage is { } map ? map(call, page) : page;
    }

    public ConcurrentQueue<(string StartHistoryId, string? PageToken)> HistoryCalls { get; } = new();

    /// <summary>Runs after the n-th (1-based) history call returned.</summary>
    public Func<int, Task>? AfterHistory { get; set; }

    /// <summary>Rejects the n-th (1-based) history call's page token when it returns true.</summary>
    public Func<int, string?, bool>? RejectHistoryPageToken { get; set; }

    public async Task<HistoryPage> ListHistoryAsync(string startHistoryId, string? pageToken, CancellationToken ct)
    {
        HistoryCalls.Enqueue((startHistoryId, pageToken));
        if (RejectHistoryPageToken?.Invoke(HistoryCalls.Count, pageToken) == true)
        {
            throw new GmailInvalidPageTokenException("Invalid page token.");
        }

        var page = await inner.ListHistoryAsync(startHistoryId, pageToken, ct);
        if (AfterHistory is { } hook)
        {
            await hook(HistoryCalls.Count);
        }

        return page;
    }

    public ConcurrentQueue<string> LabelTotalCalls { get; } = new();

    /// <summary>Runs before each label total read, for example to hold it so a concurrent caller would overlap.</summary>
    public Func<string, CancellationToken, Task>? BeforeLabelTotal { get; set; }

    private int labelTotalsInFlight;

    /// <summary>Fails a label total read started while another is in flight, as the Google client's scoped DbContext does.</summary>
    public async Task<long> GetLabelMessagesTotalAsync(string labelId, CancellationToken ct)
    {
        LabelTotalCalls.Enqueue(labelId);
        if (Interlocked.Increment(ref labelTotalsInFlight) > 1)
        {
            Interlocked.Decrement(ref labelTotalsInFlight);
            throw new InvalidOperationException("A second operation was started on this context instance before a previous operation completed.");
        }

        try
        {
            if (BeforeLabelTotal is { } before)
            {
                await before(labelId, ct);
            }

            return await inner.GetLabelMessagesTotalAsync(labelId, ct);
        }
        finally
        {
            Interlocked.Decrement(ref labelTotalsInFlight);
        }
    }

    /// <summary>Runs before each body (or body and attachments) fetch, for example to hold it and measure how many run at once.</summary>
    public Func<string, CancellationToken, Task>? BeforeBody { get; set; }

    public async Task<GmailMessageBody?> GetMessageBodyAsync(string id, CancellationToken ct)
    {
        if (BeforeBody is { } before)
        {
            await before(id, ct);
        }

        return await inner.GetMessageBodyAsync(id, ct);
    }

    public ConcurrentQueue<string> ContentCalls { get; } = new();

    public async Task<GmailMessageContent?> GetMessageContentAsync(string id, CancellationToken ct)
    {
        ContentCalls.Enqueue(id);
        if (BeforeBody is { } before)
        {
            await before(id, ct);
        }

        return await inner.GetMessageContentAsync(id, ct);
    }

    public ConcurrentQueue<string> AttachmentContentCalls { get; } = new();

    public Task<byte[]?> GetAttachmentContentAsync(string messageId, string attachmentId, CancellationToken ct)
    {
        AttachmentContentCalls.Enqueue(attachmentId);
        return inner.GetAttachmentContentAsync(messageId, attachmentId, ct);
    }

    public ConcurrentQueue<string> ThreadCalls { get; } = new();

    /// <summary>Runs before each thread lookup is passed on, after it is counted.</summary>
    public Func<string, CancellationToken, Task>? BeforeThread { get; set; }

    public async Task<GmailThreadSummary?> GetThreadSummaryAsync(string threadId, CancellationToken ct)
    {
        ThreadCalls.Enqueue(threadId);
        if (BeforeThread is { } before)
        {
            await before(threadId, ct);
        }

        return await inner.GetThreadSummaryAsync(threadId, ct);
    }

    /// <summary>Runs after the label list is read and before it is returned.</summary>
    public Func<Task>? AfterListLabels { get; set; }

    public async Task<IReadOnlyList<GmailLabel>> ListLabelsAsync(CancellationToken ct)
    {
        var labels = await inner.ListLabelsAsync(ct);
        if (AfterListLabels is { } after)
        {
            await after();
        }

        return labels;
    }

    public ConcurrentQueue<string> CreateLabelCalls { get; } = new();

    public Task<GmailLabel> CreateLabelAsync(string name, CancellationToken ct)
    {
        CreateLabelCalls.Enqueue(name);
        return inner.CreateLabelAsync(name, ct);
    }

    public ConcurrentQueue<IReadOnlyList<string>> BatchModifyCalls { get; } = new();

    /// <summary>Runs before the n-th (1-based) batch modify, with its ids, reaches the fake, for example to throw a rate limit.</summary>
    public Func<int, IReadOnlyList<string>, Task>? BeforeBatchModify { get; set; }

    public async Task BatchModifyAsync(
        IReadOnlyList<string> ids, IReadOnlyList<string> addLabelIds, IReadOnlyList<string> removeLabelIds, CancellationToken ct)
    {
        BatchModifyCalls.Enqueue([.. ids]);
        if (BeforeBatchModify is { } before)
        {
            await before(BatchModifyCalls.Count, ids);
        }

        await inner.BatchModifyAsync(ids, addLabelIds, removeLabelIds, ct);
    }

    public Task<IReadOnlyList<GmailFilter>> ListFiltersAsync(CancellationToken ct) => inner.ListFiltersAsync(ct);

    public Task<GmailFilter> CreateFilterAsync(GmailFilterCriteria criteria, GmailFilterAction action, CancellationToken ct) =>
        inner.CreateFilterAsync(criteria, action, ct);

    public Task DeleteFilterAsync(string id, CancellationToken ct) => inner.DeleteFilterAsync(id, ct);

    public async Task<IReadOnlyList<GmailMessageMetadata>> GetMessagesMetadataAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        MetadataCalls.Enqueue([.. ids]);
        var result = await inner.GetMessagesMetadataAsync(ids, ct);
        if (AfterMetadata is { } hook)
        {
            await hook(MetadataCalls.Count);
        }

        return result;
    }

    /// <summary>Runs after the n-th (1-based) labels-only call returned.</summary>
    public Func<int, Task>? AfterLabels { get; set; }

    public async Task<IReadOnlyList<GmailMessageLabels>> GetMessagesLabelsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        LabelsCalls.Enqueue([.. ids]);
        var result = await inner.GetMessagesLabelsAsync(ids, ct);
        if (AfterLabels is { } hook)
        {
            await hook(LabelsCalls.Count);
        }

        return result;
    }
}
