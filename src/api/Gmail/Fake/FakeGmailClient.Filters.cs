namespace GmailOrganiser.Gmail.Fake;

public sealed partial class FakeGmailClient
{
    private readonly FakeFilterStore filters = new();

    public async Task<IReadOnlyList<GmailFilter>> ListFiltersAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return await RetryAsync(() => filters.All, ct).ConfigureAwait(false);
    }

    public async Task<GmailFilter> CreateFilterAsync(GmailFilterCriteria criteria, GmailFilterAction action, CancellationToken ct)
    {
        GmailFilter.EnsureValidCreate(criteria, action);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return await RetryAsync(() => filters.Create(criteria, action), ct).ConfigureAwait(false);
    }

    public async Task DeleteFilterAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await RetryAsync(() =>
        {
            filters.Delete(id);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Puts a deleted filter back with its id, as the user re-creating it in Gmail would look to a sync.</summary>
    public void RestoreFilter(GmailFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        lock (gate)
        {
            filters.Restore(filter);
        }
    }
}
