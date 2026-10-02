using System.Net;
using Google;
using Google.Apis.Requests;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Gmail;

/// <summary>The outcome of one batch attempt: results so far, and the items to retry (rate-limited or a transient 5xx).</summary>
public sealed record GmailBatchAttempt<TItem, TResult>(IReadOnlyList<TResult> Succeeded, IReadOnlyList<TItem> Retry);

/// <summary>
/// Exponential backoff for Gmail rate limits (HTTP 429, or 403 with reason <c>rateLimitExceeded</c> /
/// <c>userRateLimitExceeded</c>): 1 s base doubling to a 64 s cap, with equal jitter (half fixed, half random) so a
/// retry never fires immediately. Batch items that fail with a transient 5xx are retried the same way. Shared by every
/// Gmail read; singleton.
/// </summary>
public sealed class GmailRetryPolicy(IOptions<GmailOptions> options, TimeProvider time, Random? random = null)
{
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(64);

    private static readonly string[] RateLimitReasons = ["rateLimitExceeded", "userRateLimitExceeded"];
    private readonly Random random = random ?? Random.Shared;

    public int MaxAttempts => options.Value.MaxRetryAttempts;

    public static bool IsRateLimited(HttpStatusCode status, RequestError? error) =>
        status == HttpStatusCode.TooManyRequests
        || (status == HttpStatusCode.Forbidden
            && error?.Errors?.Any(e => RateLimitReasons.Contains(e.Reason, StringComparer.Ordinal)) == true);

    public static bool IsRateLimited(Exception ex) =>
        ex is GoogleApiException api && IsRateLimited(api.HttpStatusCode, api.Error);

    /// <summary>A server-side error Gmail asks clients to back off and retry (e.g. 500 <c>backendError</c>).</summary>
    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>Whether a failed batch item (or a whole batch call) should be re-sent after a backoff.</summary>
    public static bool IsRetryable(HttpStatusCode status, RequestError? error) =>
        IsRateLimited(status, error) || IsTransient(status);

    /// <summary>The wait after failed attempt <paramref name="attempt"/> (1-based): between half and all of min(cap, base·2^(n-1)).</summary>
    public TimeSpan GetDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var exponent = Math.Min(attempt - 1, 16);
        var ceiling = Math.Min(BaseDelay.TotalMilliseconds * (1L << exponent), MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds((ceiling / 2) + (random.NextDouble() * ceiling / 2));
    }

    /// <summary>Runs a single request, retrying it while Gmail rate-limits it.</summary>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRateLimited(ex))
            {
                if (attempt >= MaxAttempts)
                {
                    throw new GmailRateLimitedException($"Gmail rate-limited the request {attempt} times; try again later.", ex);
                }

                await Task.Delay(GetDelay(attempt), time, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Sends <paramref name="items"/> in one batch, then re-sends only the items to retry after a backoff,
    /// so one 429 or 5xx does not re-spend the quota of the whole batch.
    /// </summary>
    public async Task<IReadOnlyList<TResult>> ExecuteBatchAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        Func<IReadOnlyList<TItem>, CancellationToken, Task<GmailBatchAttempt<TItem, TResult>>> send,
        CancellationToken ct)
    {
        var results = new List<TResult>(items.Count);
        var pending = items;
        for (var attempt = 1; pending.Count > 0; attempt++)
        {
            var outcome = await send(pending, ct).ConfigureAwait(false);
            results.AddRange(outcome.Succeeded);
            pending = outcome.Retry;
            if (pending.Count == 0)
            {
                break;
            }

            if (attempt >= MaxAttempts)
            {
                throw new GmailRateLimitedException(
                    $"Gmail rate-limited or failed {pending.Count} batch item(s) {attempt} times; try again later.");
            }

            await Task.Delay(GetDelay(attempt), time, ct).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>A Gmail-shaped API error, as the Google client raises it (used by the fake for fault injection).</summary>
    public static GoogleApiException CreateApiException(HttpStatusCode status, string? reason = null) =>
        new("gmail", $"Gmail returned {(int)status}.")
        {
            HttpStatusCode = status,
            Error = new RequestError
            {
                Code = (int)status,
                Errors = reason is null ? [] : [new SingleError { Reason = reason }],
            },
        };
}
