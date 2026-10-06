using GmailOrganiser.Gmail;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit;

/// <summary>
/// The real token and settings stores share the scope's one <c>AppDbContext</c>, and the analysis run calls
/// <see cref="GoogleGmailClient"/> from parallel body fetches. These stores record how many of their reads are in
/// flight at once and hold each read open, so any overlap is seen. Every call fails before it would reach Google.
/// </summary>
public sealed class GoogleGmailClientConcurrencyTests
{
    private const int Callers = 8;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly OverlapProbe probe = new();

    [Fact]
    public async Task Concurrent_body_fetches_never_overlap_the_token_reads()
    {
        var client = CreateClient(new OAuthToken(
            "user@example.com", "synthetic-refresh-token", [.. GmailScopes.All], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        {
            ReauthRequired = true,
        });

        await FetchConcurrentlyAsync(client);

        probe.Reads.ShouldBe(Callers);
        probe.MaxInFlight.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_body_fetches_never_overlap_the_token_and_settings_reads()
    {
        // A connected token but no Google client configured: each call reads both stores, then stops.
        var client = CreateClient(new OAuthToken(
            "user@example.com", "synthetic-refresh-token", [.. GmailScopes.All], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

        await FetchConcurrentlyAsync(client);

        probe.Reads.ShouldBe(Callers * 2);
        probe.MaxInFlight.ShouldBe(1);
    }

    [Fact]
    public async Task A_cancelled_caller_stops_waiting_for_the_gate()
    {
        var client = CreateClient(new OAuthToken(
            "user@example.com", "synthetic-refresh-token", [.. GmailScopes.All], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        {
            ReauthRequired = true,
        });
        probe.Hold = Timeout.InfiniteTimeSpan;
        using var holder = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var first = client.GetMessageBodyAsync("m-0", holder.Token);
        await probe.Entered.Task.WaitAsync(Ct);

        using var waiter = new CancellationTokenSource();
        var second = client.GetMessageBodyAsync("m-1", waiter.Token);
        await waiter.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(second.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        await holder.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(first);
    }

    private static async Task FetchConcurrentlyAsync(GoogleGmailClient client)
    {
        var calls = Enumerable.Range(0, Callers)
            .Select(i => Task.Run(() => client.GetMessageBodyAsync($"m-{i}", Ct), Ct))
            .ToArray();
        foreach (var call in calls)
        {
            await Should.ThrowAsync<GmailNotConnectedException>(call);
        }
    }

    private GoogleGmailClient CreateClient(OAuthToken token)
    {
        var gmail = Options.Create(new GmailOptions());
        return new GoogleGmailClient(
            new ProbedTokenStore(token, probe),
            new GoogleClientService(
                new ProbedSettingsStore(probe),
                new EphemeralDataProtectionProvider(),
                Options.Create(new SettingsEnvOptions()),
                NullLogger<GoogleClientService>.Instance),
            new GmailRetryPolicy(gmail, TimeProvider.System),
            new GmailQuotaLimiter(gmail, TimeProvider.System),
            gmail,
            NullLogger<GoogleGmailClient>.Instance);
    }

    private sealed class OverlapProbe
    {
        private int inFlight;
        private int maxInFlight;
        private int reads;

        public TimeSpan Hold { get; set; } = TimeSpan.FromMilliseconds(20);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxInFlight => Volatile.Read(ref maxInFlight);

        public int Reads => Volatile.Read(ref reads);

        public async Task<T> ReadAsync<T>(T value, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref inFlight);
            Interlocked.Increment(ref reads);
            int seen;
            while (now > (seen = Volatile.Read(ref maxInFlight)) && Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen)
            {
            }

            Entered.TrySetResult();
            try
            {
                await Task.Delay(Hold, ct);
                return value;
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        }
    }

    private sealed class ProbedTokenStore(OAuthToken token, OverlapProbe probe) : ITokenStore
    {
        public Task<OAuthToken?> GetAsync(CancellationToken ct = default) => probe.ReadAsync<OAuthToken?>(token, ct);

        public Task SaveAsync(string accountEmail, string refreshToken, IReadOnlyList<string> scopes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task MarkReauthRequiredAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ProbedSettingsStore(OverlapProbe probe) : ISettingsStore
    {
        private readonly InMemorySettingsStore inner = new();

        public Task<AppSettings> GetAsync(CancellationToken ct = default) => probe.ReadAsync(inner.Current, ct);

        public Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken ct = default) =>
            inner.UpdateAsync(change, ct);
    }
}
