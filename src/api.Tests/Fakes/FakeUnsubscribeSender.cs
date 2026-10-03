using System.Collections.Concurrent;
using GmailOrganiser.CleanUp.Unsubscribe;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>Records one-click POSTs instead of sending them; answers with <see cref="Respond"/> (200 by default).</summary>
public sealed class FakeUnsubscribeSender : IUnsubscribeSender
{
    private readonly ConcurrentQueue<Uri> sent = new();

    public Func<Uri, CancellationToken, Task<UnsubscribeSendResult>> Respond { get; set; } =
        (_, _) => Task.FromResult(new UnsubscribeSendResult(true, 200));

    public IReadOnlyList<Uri> Sent => [.. sent];

    public Task<UnsubscribeSendResult> SendOneClickAsync(Uri url, CancellationToken ct)
    {
        sent.Enqueue(url);
        return Respond(url, ct);
    }
}
