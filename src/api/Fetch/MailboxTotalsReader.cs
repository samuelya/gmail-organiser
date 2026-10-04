using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using Google;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Fetch;

/// <param name="Inbox">The Inbox label's message total.</param>
/// <param name="AllMail">The profile's message total minus Spam and Trash.</param>
public sealed record MailboxTotals(long Inbox, long AllMail, DateTimeOffset MeasuredAt);

/// <summary>
/// The Gmail mailbox totals the fetch status measures stored coverage against (DESIGN §6.1), cached for
/// <see cref="Ttl"/> per connected account so status polls don't spend quota. Each measurement is written to
/// <c>fetch_state</c>, which serves as the last known totals after a restart or while Gmail can't be read.
/// </summary>
public sealed partial class MailboxTotalsReader(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<MailboxTotalsReader> logger)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile CachedTotals? cached;

    /// <summary>
    /// The cached totals of the connected account, measured again when older than <see cref="Ttl"/> or on
    /// <paramref name="refresh"/>. Null when Gmail is not connected, the local data belongs to another account,
    /// or Gmail can't be read (any API error, rate limit, network error or timeout); the caller then uses the last known
    /// totals. Only the caller's own cancellation propagates.
    /// </summary>
    public async Task<MailboxTotals?> GetAsync(bool refresh, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var connected = await services.GetRequiredService<ITokenStore>().GetAsync(ct);
        if (connected is null || string.IsNullOrEmpty(connected.RefreshToken))
        {
            return null;
        }

        var db = services.GetRequiredService<AppDbContext>();
        var local = await db.FetchState.AsNoTracking()
            .Where(s => s.Id == FetchStateRow.SingletonId)
            .Select(s => s.AccountEmail)
            .SingleAsync(ct);
        if (AccountGuard.Compare(local, connected.AccountEmail).IsMismatch)
        {
            return null;
        }

        await gate.WaitAsync(ct);
        try
        {
            if (!refresh && Fresh(connected.AccountEmail) is { } hit)
            {
                return hit;
            }

            var gmail = services.GetRequiredService<IGmailClient>();
            var totals = await MeasureAsync(gmail, await gmail.GetProfileAsync(ct), ct);
            await db.FetchState.ExecuteUpdateAsync(set => set
                .SetProperty(f => f.InboxTotal, totals.Inbox)
                .SetProperty(f => f.AllMailTotal, totals.AllMail), ct);
            return totals;
        }
        catch (Exception ex) when (
            ex is GmailNotConnectedException or GmailRateLimitedException or HttpRequestException or GoogleApiException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            LogMeasureFailed(logger, ex.GetType().Name);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Measures the totals of <paramref name="profile"/>'s mailbox (three <c>labels.get</c> calls) and caches them.
    /// Gmail errors propagate: the mailbox fetch that calls this fails and retries as for any other call.
    /// </summary>
    public async Task<MailboxTotals> MeasureAsync(IGmailClient gmail, GmailProfile profile, CancellationToken ct)
    {
        var labelTotals = await Task.WhenAll(
            gmail.GetLabelMessagesTotalAsync(MailboxFetchJob.InboxLabelId, ct),
            gmail.GetLabelMessagesTotalAsync(MailboxFetchJob.SpamLabelId, ct),
            gmail.GetLabelMessagesTotalAsync(MailboxFetchJob.TrashLabelId, ct));
        var totals = new MailboxTotals(
            labelTotals[0], Math.Max(0, profile.MessagesTotal - labelTotals[1] - labelTotals[2]), time.GetUtcNow());
        cached = new CachedTotals(profile.EmailAddress, totals);
        return totals;
    }

    private MailboxTotals? Fresh(string account) =>
        cached is { } c
        && AccountGuard.Compare(c.Account, account).Status != AccountCheckStatus.Mismatch
        && time.GetUtcNow() - c.Totals.MeasuredAt < Ttl
            ? c.Totals
            : null;

    private sealed record CachedTotals(string Account, MailboxTotals Totals);

    [LoggerMessage(Level = LogLevel.Information, Message = "Gmail mailbox totals not measured ({Reason}); using the last known totals")]
    private static partial void LogMeasureFailed(ILogger logger, string reason);
}
