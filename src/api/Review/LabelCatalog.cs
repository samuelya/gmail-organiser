using GmailOrganiser.Gmail;

namespace GmailOrganiser.Review;

/// <summary>
/// The connected account's Gmail label list cached for <see cref="CacheFor"/>; the shared label-id resolver for
/// review, apply and rules. The cache belongs to one account: a disconnect or another account never sees it.
/// Whoever creates or renames a label calls <see cref="Invalidate"/>.
/// </summary>
public sealed class LabelCatalog(IServiceScopeFactory scopes, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Lock store = new();
    private Cached? cached;
    private long generation;

    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    public Task<IReadOnlyList<GmailLabel>> GetAsync(CancellationToken ct) => LoadAsync(bypassCache: false, ct);

    /// <summary>Drops the cache and loads the list from Gmail, even when another load is in flight.</summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    public Task<IReadOnlyList<GmailLabel>> RefreshAsync(CancellationToken ct)
    {
        Invalidate();
        return LoadAsync(bypassCache: true, ct);
    }

    /// <summary>The label named <paramref name="name"/> (exact match first, then case-insensitive), or null.</summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    public async Task<GmailLabel?> FindByNameAsync(string name, CancellationToken ct) =>
        GmailLabel.FindByName(await GetAsync(ct), name);

    /// <summary>Drops the cache; a load already in flight does not store its (possibly stale) result.</summary>
    public void Invalidate()
    {
        lock (store)
        {
            generation++;
            cached = null;
        }
    }

    public void Dispose() => gate.Dispose();

    private async Task<IReadOnlyList<GmailLabel>> LoadAsync(bool bypassCache, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var token = await scope.ServiceProvider.GetRequiredService<ITokenStore>().GetAsync(ct);
        if (token is null || token.ReauthRequired)
        {
            Invalidate();
            throw new GmailNotConnectedException("Gmail is not connected.");
        }

        var account = token.AccountEmail;
        if (!bypassCache && Fresh(account) is { } labels)
        {
            return labels;
        }

        await gate.WaitAsync(ct);
        try
        {
            if (!bypassCache && Fresh(account) is { } loaded)
            {
                return loaded;
            }

            long started;
            lock (store)
            {
                started = generation;
            }

            var list = await scope.ServiceProvider.GetRequiredService<IGmailClient>().ListLabelsAsync(ct);
            lock (store)
            {
                if (generation == started)
                {
                    cached = new Cached(account, list, time.GetUtcNow() + CacheFor);
                }
            }

            return list;
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyList<GmailLabel>? Fresh(string account)
    {
        lock (store)
        {
            return cached is { } c && string.Equals(c.Account, account, StringComparison.OrdinalIgnoreCase) && time.GetUtcNow() < c.ExpiresAt
                ? c.Labels
                : null;
        }
    }

    private sealed record Cached(string Account, IReadOnlyList<GmailLabel> Labels, DateTimeOffset ExpiresAt);
}
