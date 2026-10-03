namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// Runs a synchronous parse on the thread pool and gives up after a timeout with a <see cref="TimeoutException"/>, so a
/// hostile file can't hold up a job. WaitAsync returns on timeout even while the parser is stuck inside one item; the parse
/// sees the token between pages, rows, paragraphs or slides.
/// </summary>
internal static class ParseTimeout
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(30);

    public static async Task<T> RunAsync<T>(Func<CancellationToken, T> parse, TimeSpan timeout, string what, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // A non-positive timeout cancels before the parse starts, so it always times out (a test seam, not a race).
        if (timeout <= TimeSpan.Zero) cts.Cancel();
        else cts.CancelAfter(timeout);
        try
        {
            return await Task.Run(() => parse(cts.Token), cts.Token).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Reading the {what} took longer than the parse timeout.");
        }
    }
}
