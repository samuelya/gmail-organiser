using Microsoft.Extensions.Options;

namespace GmailOrganiser.Gmail;

/// <summary>
/// Throttles Gmail calls to <see cref="GmailOptions.QuotaUnitsPerSecond"/> before they are sent. A token bucket that
/// refills one second after each grant (a sliding one-second window), so no second ever admits more than the budget,
/// not even the first. Singleton: the quota is per user, shared by every job.
/// </summary>
public sealed class GmailQuotaLimiter(IOptions<GmailOptions> options, TimeProvider time)
{
    /// <summary>Cost of <c>messages.list</c> and <c>messages.get</c> in Gmail quota units.</summary>
    public const int MessageCallUnits = 5;

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Lock sync = new();
    private readonly Queue<(DateTimeOffset At, int Units)> grants = new();
    private int unitsInWindow;

    private int Budget => options.Value.QuotaUnitsPerSecond;

    /// <summary>Waits until <paramref name="units"/> fit in the current second's budget, then spends them.</summary>
    public async Task AcquireAsync(int units, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (!TryAcquire(units, out var wait))
            {
                await Task.Delay(wait, time, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Spends <paramref name="units"/> if they fit now; otherwise says how long until they might. A batch spends its
    /// whole cost in one call, right before it is sent; options validation keeps a full batch within the budget, so
    /// a larger request is a bug and throws rather than waiting forever.
    /// </summary>
    public bool TryAcquire(int units, out TimeSpan retryAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(units, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(units, Budget);
        lock (sync)
        {
            var now = time.GetUtcNow();
            while (grants.Count > 0 && grants.Peek().At + Window <= now)
            {
                unitsInWindow -= grants.Dequeue().Units;
            }

            if (unitsInWindow + units <= Budget)
            {
                grants.Enqueue((now, units));
                unitsInWindow += units;
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = grants.Peek().At + Window - now;
            return false;
        }
    }
}
