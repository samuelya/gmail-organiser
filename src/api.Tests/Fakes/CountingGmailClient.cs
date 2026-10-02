using System.Collections.Concurrent;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>Records every call to the wrapped <see cref="FakeGmailClient"/> and runs a hook after each metadata call.</summary>
public sealed class CountingGmailClient(FakeGmailClient inner) : IGmailClient
{
    public FakeGmailClient Inner => inner;

    public ConcurrentQueue<MessageListQuery> ListCalls { get; } = new();

    public ConcurrentQueue<IReadOnlyList<string>> MetadataCalls { get; } = new();

    /// <summary>Runs after the n-th (1-based) metadata call returned.</summary>
    public Func<int, Task>? AfterMetadata { get; set; }

    public Task<GmailProfile> GetProfileAsync(CancellationToken ct) => inner.GetProfileAsync(ct);

    public Task<MessageIdPage> ListMessageIdsAsync(MessageListQuery query, CancellationToken ct)
    {
        ListCalls.Enqueue(query);
        return inner.ListMessageIdsAsync(query, ct);
    }

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
}
