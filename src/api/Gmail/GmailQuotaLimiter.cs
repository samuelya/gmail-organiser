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

    /// <summary>Cost of <c>labels.get</c> and <c>labels.list</c> in Gmail quota units.</summary>
    public const int LabelCallUnits = 1;

    /// <summary>Cost of <c>history.list</c> in Gmail quota units.</summary>
    public const int HistoryCallUnits = 2;

    /// <summary>Cost of <c>labels.create</c> in Gmail quota units.</summary>
    public const int LabelCreateUnits = 5;

    /// <summary>Cost of <c>messages.batchModify</c> in Gmail quota units; the most expensive call the app makes.</summary>
    public const int BatchModifyUnits = 50;

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
    /// whole cost in one call, right before it is sent. A call costing more than the budget (a <c>batchModify</c> under
    /// a low budget) is clamped to the budget, so it waits for an empty window and then takes the whole second.
    /// </summary>
    public bool TryAcquire(int units, out TimeSpan retryAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(units, 1);
        units = Math.Min(units, Budget);
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
