using System.Net;
using GmailOrganiser.Gmail;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Unit;

/// <summary><see cref="GmailRetryPolicy"/> and <see cref="GmailQuotaLimiter"/>, driven by a fake clock.</summary>
public sealed class GmailThrottlingTests
{
    private readonly FakeTimeProvider time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 8)]
    [InlineData(7, 64)]
    [InlineData(8, 64)]
    [InlineData(40, 64)]
    public void GetDelay_is_exponential_with_equal_jitter_and_capped(int attempt, int ceilingSeconds)
    {
        var policy = Policy(maxAttempts: 8, new Random(7));

        for (var i = 0; i < 50; i++)
        {
            var delay = policy.GetDelay(attempt);
            delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(ceilingSeconds / 2.0));
            delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(ceilingSeconds));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, null, true)]
    [InlineData(HttpStatusCode.Forbidden, "rateLimitExceeded", true)]
    [InlineData(HttpStatusCode.Forbidden, "userRateLimitExceeded", true)]
    [InlineData(HttpStatusCode.Forbidden, "insufficientPermissions", false)]
    [InlineData(HttpStatusCode.Forbidden, null, false)]
    [InlineData(HttpStatusCode.InternalServerError, null, false)]
    public void IsRateLimited_matches_429_and_rate_limit_403s(HttpStatusCode status, string? reason, bool expected) =>
        GmailRetryPolicy.IsRateLimited(GmailRetryPolicy.CreateApiException(status, reason)).ShouldBe(expected);

    [Fact]
    public async Task ExecuteAsync_retries_rate_limits_after_a_backoff_then_succeeds()
    {
        var policy = Policy(maxAttempts: 8);
        var calls = 0;

        var task = policy.ExecuteAsync(_ => ++calls < 3
            ? throw GmailRetryPolicy.CreateApiException(HttpStatusCode.TooManyRequests)
            : Task.FromResult("ok"), Ct);

        (await DriveAsync(task)).ShouldBe("ok");
        calls.ShouldBe(3);
        (time.GetUtcNow() - Start).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(0.5 + 1));
    }

    [Fact]
    public async Task ExecuteAsync_does_not_retry_other_errors()
    {
        var policy = Policy(maxAttempts: 8);
        var calls = 0;

        await Should.ThrowAsync<Google.GoogleApiException>(() => policy.ExecuteAsync<string>(_ =>
        {
            calls++;
            throw GmailRetryPolicy.CreateApiException(HttpStatusCode.Forbidden, "insufficientPermissions");
        }, Ct));
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_throws_GmailRateLimitedException_after_the_last_attempt()
    {
        var policy = Policy(maxAttempts: 3);
        var calls = 0;

        var task = policy.ExecuteAsync<string>(_ =>
        {
            calls++;
            throw GmailRetryPolicy.CreateApiException(HttpStatusCode.Forbidden, "userRateLimitExceeded");
        }, Ct);

        await Should.ThrowAsync<GmailRateLimitedException>(() => DriveAsync(task));
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task ExecuteBatchAsync_resends_only_the_rate_limited_items()
    {
        var policy = Policy(maxAttempts: 8);
        var sent = new List<IReadOnlyList<int>>();

        var task = policy.ExecuteBatchAsync<int, int>([1, 2, 3, 4], (pending, _) =>
        {
            sent.Add(pending);
            // First attempt: 2 and 4 are rate-limited; second attempt: 4 again; third: all succeed.
            IReadOnlyList<int> limited = sent.Count switch { 1 => [2, 4], 2 => [4], _ => [] };
            return Task.FromResult(new GmailBatchAttempt<int, int>([.. pending.Except(limited).Select(i => i * 10)], limited));
        }, Ct);

        (await DriveAsync(task)).ShouldBe([10, 30, 20, 40]);
        sent.Count.ShouldBe(3);
        sent[1].ShouldBe([2, 4]);
        sent[2].ShouldBe([4]);
    }

    [Fact]
    public async Task ExecuteBatchAsync_gives_up_after_the_last_attempt()
    {
        var policy = Policy(maxAttempts: 2);

        var task = policy.ExecuteBatchAsync<int, int>([1, 2], (pending, _) =>
            Task.FromResult(new GmailBatchAttempt<int, int>([], pending)), Ct);

        await Should.ThrowAsync<GmailRateLimitedException>(() => DriveAsync(task));
    }

    [Fact]
    public void QuotaLimiter_admits_at_most_the_budget_per_second()
    {
        var limiter = Limiter(budget: 200);

        var admitted = Enumerable.Range(0, 100).Count(i => limiter.TryAcquire(GmailQuotaLimiter.MessageCallUnits, out _));
        admitted.ShouldBe(40);

        limiter.TryAcquire(GmailQuotaLimiter.MessageCallUnits, out var wait).ShouldBeFalse();
        wait.ShouldBe(TimeSpan.FromSeconds(1));

        time.Advance(TimeSpan.FromMilliseconds(999));
        limiter.TryAcquire(GmailQuotaLimiter.MessageCallUnits, out _).ShouldBeFalse();

        time.Advance(TimeSpan.FromMilliseconds(1));
        Enumerable.Range(0, 100).Count(i => limiter.TryAcquire(GmailQuotaLimiter.MessageCallUnits, out _)).ShouldBe(40);
    }

    [Fact]
    public void QuotaLimiter_frees_units_one_second_after_each_grant()
    {
        var limiter = Limiter(budget: 10);
        limiter.TryAcquire(5, out _).ShouldBeTrue();
        time.Advance(TimeSpan.FromMilliseconds(400));
        limiter.TryAcquire(5, out _).ShouldBeTrue();
        limiter.TryAcquire(5, out var wait).ShouldBeFalse();
        wait.ShouldBe(TimeSpan.FromMilliseconds(600));

        time.Advance(wait);
        limiter.TryAcquire(5, out _).ShouldBeTrue();
        limiter.TryAcquire(5, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task QuotaLimiter_AcquireAsync_waits_for_the_next_second()
    {
        var limiter = Limiter(budget: 10);
        await limiter.AcquireAsync(10, Ct);

        var task = limiter.AcquireAsync(5, Ct);
        task.IsCompleted.ShouldBeFalse();

        await DriveAsync(task.ContinueWith(_ => true, Ct, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        (time.GetUtcNow() - Start).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void QuotaLimiter_rejects_requests_larger_than_the_budget() =>
        Should.Throw<ArgumentOutOfRangeException>(() => Limiter(budget: 10).TryAcquire(11, out _));

    private DateTimeOffset Start { get; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private GmailRetryPolicy Policy(int maxAttempts, Random? random = null) =>
        new(Options.Create(new GmailOptions { MaxRetryAttempts = maxAttempts }), time, random);

    private GmailQuotaLimiter Limiter(int budget) =>
        new(Options.Create(new GmailOptions { QuotaUnitsPerSecond = budget }), time);

    /// <summary>Advances the fake clock in small steps until <paramref name="task"/> completes (no real backoff sleeps).</summary>
    private async Task<T> DriveAsync<T>(Task<T> task)
    {
        for (var i = 0; i < 2000 && !task.IsCompleted; i++)
        {
            await Task.Delay(1, Ct);
            if (!task.IsCompleted)
            {
                time.Advance(TimeSpan.FromMilliseconds(250));
            }
        }

        return await task;
    }
}
