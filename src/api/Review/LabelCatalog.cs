using GmailOrganiser.Gmail;

namespace GmailOrganiser.Review;

/// <summary>
/// The Gmail label list cached for <see cref="CacheFor"/>; the shared label-id resolver for review, apply and rules.
/// Whoever creates or renames a label calls <see cref="Invalidate"/>.
/// </summary>
public sealed class LabelCatalog(IServiceScopeFactory scopes, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim gate = new(1, 1);
    private Cached? cached;

    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    public async Task<IReadOnlyList<GmailLabel>> GetAsync(CancellationToken ct)
    {
        if (Fresh() is { } labels)
        {
            return labels;
        }

        await gate.WaitAsync(ct);
        try
        {
            if (Fresh() is { } loaded)
            {
                return loaded;
            }

            await using var scope = scopes.CreateAsyncScope();
            var list = await scope.ServiceProvider.GetRequiredService<IGmailClient>().ListLabelsAsync(ct);
            Volatile.Write(ref cached, new Cached(list, time.GetUtcNow() + CacheFor));
            return list;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The label named <paramref name="name"/> (exact match first, then case-insensitive), or null.</summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    public async Task<GmailLabel?> FindByNameAsync(string name, CancellationToken ct) =>
        GmailLabel.FindByName(await GetAsync(ct), name);

    public void Invalidate() => Volatile.Write(ref cached, null);

    public void Dispose() => gate.Dispose();

    private IReadOnlyList<GmailLabel>? Fresh() =>
        Volatile.Read(ref cached) is { } c && time.GetUtcNow() < c.ExpiresAt ? c.Labels : null;

    private sealed record Cached(IReadOnlyList<GmailLabel> Labels, DateTimeOffset ExpiresAt);
}
