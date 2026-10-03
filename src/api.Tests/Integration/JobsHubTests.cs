using System.Data.Common;
using System.Net;
using System.Threading.Channels;
using GmailOrganiser.Claude;
using GmailOrganiser.Data;
using GmailOrganiser.Jobs;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class JobsHubTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    private readonly CountingJobState state = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
    private readonly Channel<JobDto[]> snapshots = Channel.CreateUnbounded<JobDto[]>();
    private readonly Channel<JobDto> changes = Channel.CreateUnbounded<JobDto>();
    private readonly Channel<ExternalReviewDto> reviews = Channel.CreateUnbounded<ExternalReviewDto>();
    private WebApplicationFactory<Program> host = null!;
    private HubConnection? connection;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Jobs.ExecuteDeleteAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }

        if (host is not null)
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Connect_sends_a_snapshot_of_the_active_jobs()
    {
        host = CountingJobHandler.CreateHost(factory, state);
        var job = await EnqueueAsync();

        await ConnectAsync(ApiFactory.AllowedOrigin);

        var snapshot = await ReadAsync(snapshots);
        snapshot.ShouldHaveSingleItem().Id.ShouldBe(job.Id);
        snapshot[0].Status.ShouldBe("queued");
    }

    [Fact]
    public async Task A_running_job_streams_changes_ending_in_completed()
    {
        host = CountingJobHandler.CreateHost(factory, state);
        await ConnectAsync(ApiFactory.AllowedOrigin);
        (await ReadAsync(snapshots)).ShouldBeEmpty();

        var job = await EnqueueAsync();
        var runner = ActivatorUtilities.CreateInstance<JobRunner>(host.Services);
        (await runner.ClaimAsync(Ct)).ShouldHaveSingleItem().ShouldBe(job.Id);
        await runner.RunAsync(job.Id, Ct);

        var received = new List<JobDto>();
        do
        {
            received.Add(await ReadAsync(changes));
        }
        while (received[^1].Status != "completed");

        received.ShouldAllBe(e => e.Id == job.Id);
        received.Select(e => e.Status).Distinct().ShouldBe(["queued", "running", "completed"]);
        received[^1].Progress.ShouldBe(new JobProgress(5, 5, "step 5"));
    }

    [Fact]
    public async Task Progress_is_throttled_per_interval_and_the_latest_is_delivered()
    {
        var publisher = await ConnectFakeClockAsync();
        var job = Job("running", 0);
        var p1 = Next(job, "running", 1);
        var p2 = Next(p1, "running", 2);

        await publisher.JobChangedAsync(job, Ct);
        await publisher.JobChangedAsync(p1, Ct);
        await publisher.JobChangedAsync(p2, Ct);
        (await ReadAsync(changes)).Progress.ShouldBe(Progress(0));

        // Per-job order is preserved, so a wrongly sent Progress(1) would be read here instead.
        clock.Advance(TimeSpan.FromSeconds(1));
        (await ReadAsync(changes)).Progress.ShouldBe(Progress(2));

        clock.Advance(TimeSpan.FromSeconds(1));
        await publisher.JobChangedAsync(Next(p2, "running", 3), Ct);
        (await ReadAsync(changes)).Progress.ShouldBe(Progress(3));
    }

    [Fact]
    public async Task A_status_change_is_sent_at_once_and_drops_parked_progress()
    {
        var publisher = await ConnectFakeClockAsync();
        var job = Job("running", 0);
        var parked = Next(job, "running", 1);
        var done = Next(parked, "completed", 2);

        await publisher.JobChangedAsync(job, Ct);
        await publisher.JobChangedAsync(parked, Ct);
        await publisher.JobChangedAsync(done, Ct);

        (await ReadAsync(changes)).Progress.ShouldBe(Progress(0));
        var received = await ReadAsync(changes);
        received.Status.ShouldBe("completed");
        received.Progress.ShouldBe(Progress(2));

        clock.Advance(TimeSpan.FromSeconds(5));
        await AssertNextIsSentinelAsync(publisher, done);
    }

    [Fact]
    public async Task An_older_event_after_the_final_one_is_dropped()
    {
        var publisher = await ConnectFakeClockAsync();
        var running = Job("running", 1);
        var cancelled = Next(running, "cancelled", 1);

        await publisher.JobChangedAsync(cancelled, Ct);
        (await ReadAsync(changes)).Status.ShouldBe("cancelled");

        // A request thread's late read of the row, and a same-version copy.
        await publisher.JobChangedAsync(running, Ct);
        await publisher.JobChangedAsync(cancelled with { Status = "running" }, Ct);
        await AssertNextIsSentinelAsync(publisher, cancelled);
    }

    [Fact]
    public async Task A_cancel_committed_while_the_run_finishes_does_not_hide_the_final_state()
    {
        // #81: the finish takes its timestamp, then a Cancel with a later clock commits before the finish's UPDATE.
        Guid jobId = default;
        var cancelFirst = new BeforeJobFinishInterceptor(async () =>
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            (await WithJobsAsync(s => s.CancelAsync(jobId, Ct))).ShouldBe(JobActionResult.Ok);
        });
        host = CountingJobHandler.CreateHost(factory, state).WithWebHostBuilder(b => b.ConfigureTestServices(s => s
            .AddSingleton<TimeProvider>(clock)
            .ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(cancelFirst))));
        await ConnectAsync(ApiFactory.AllowedOrigin);
        await ReadAsync(snapshots);

        // No handler is registered for this type, so the run goes straight to FinishAsync(failed).
        jobId = (await WithJobsAsync(s => s.EnqueueAsync("test-unhandled", JobQueues.Fetch, null, Ct))).Job.Id;
        var runner = ActivatorUtilities.CreateInstance<JobRunner>(host.Services);
        (await runner.ClaimAsync(Ct)).ShouldBe([jobId]);
        await runner.RunAsync(jobId, Ct);

        cancelFirst.Fired.ShouldBeTrue();
        var received = new List<JobDto>();
        do
        {
            received.Add(await ReadAsync(changes));
        }
        while (received[^1].Status != "failed");

        received.Select(e => e.Status).ShouldBe(["queued", "running", "running", "failed"]);
        received.Select(e => e.Version).ShouldBeInOrder(SortDirection.Ascending);
        received[^1].UpdatedAt.ShouldBeLessThan(received[^2].UpdatedAt);
        (await WithJobsAsync(s => s.GetAsync(jobId, Ct))).ShouldNotBeNull().ShouldBe(received[^1]);
    }

    [Fact]
    public async Task A_stalled_client_never_blocks_the_publisher_and_order_is_kept()
    {
        var hub = new StalledHubContext();
        host = CountingJobHandler.CreateHost(factory, state)
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IHubContext<JobsHub>>(hub)));
        var publisher = host.Services.GetRequiredService<IJobProgressPublisher>();
        var running = Job("running", 0);
        var paused = Next(running, "paused", 1);
        var completed = Next(paused, "completed", 2);

        publisher.JobChangedAsync(running, Ct).IsCompletedSuccessfully.ShouldBeTrue();
        await hub.FirstSendStarted.Task.WaitAsync(EventTimeout, Ct);
        publisher.JobChangedAsync(paused, Ct).IsCompletedSuccessfully.ShouldBeTrue();
        publisher.JobChangedAsync(completed, Ct).IsCompletedSuccessfully.ShouldBeTrue();

        hub.Release.SetResult();
        var sent = new List<JobDto>();
        do
        {
            sent.Add(await ReadAsync(hub.Sent));
        }
        while (sent[^1].Status != "completed");

        sent.ShouldBe([running, paused, completed]);
    }

    [Fact]
    public async Task A_claude_review_item_change_reaches_connected_clients()
    {
        host = CountingJobHandler.CreateHost(factory, state);
        await ConnectAsync(ApiFactory.AllowedOrigin);
        (await ReadAsync(snapshots)).ShouldBeEmpty();
        var item = new ExternalReviewDto(
            Guid.NewGuid(), "group", null, "sender@example.com", "from:sender@example.com", "Synthetic subject", "reviewed",
            "mcp", "agree", "Synthetic", false, false, "Synthetic reasoning", null, "none", clock.GetUtcNow(), clock.GetUtcNow(), null);

        await host.Services.GetRequiredService<IExternalReviewNotifier>().NotifyAsync(item, Ct);

        (await ReadAsync(reviews)).ShouldBe(item);
    }

    [Fact]
    public async Task Negotiate_from_a_disallowed_origin_is_rejected()
    {
        host = CountingJobHandler.CreateHost(factory, state);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => ConnectAsync("http://evil.example.com"));

        ex.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("http://evil.example.com", HttpStatusCode.Forbidden)]
    [InlineData(ApiFactory.AllowedOrigin, HttpStatusCode.NotFound)]
    public async Task Websocket_upgrade_request_is_checked_against_the_origin_allow_list(string origin, HttpStatusCode expected)
    {
        host = CountingJobHandler.CreateHost(factory, state);
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{JobsHub.Path}?id=unknown");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Upgrade", "websocket");
        request.Headers.Add("Connection", "Upgrade");

        using var response = await client.SendAsync(request, Ct);

        // An allowed origin reaches SignalR, which answers the unknown connection id with 404.
        response.StatusCode.ShouldBe(expected);
    }

    private async Task<IJobProgressPublisher> ConnectFakeClockAsync()
    {
        host = CountingJobHandler.CreateHost(factory, state)
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        await ConnectAsync(ApiFactory.AllowedOrigin);
        await ReadAsync(snapshots);
        return host.Services.GetRequiredService<IJobProgressPublisher>();
    }

    /// <summary>Publishes a newer status change and asserts it is the next event, so nothing was sent in between.</summary>
    private async Task AssertNextIsSentinelAsync(IJobProgressPublisher publisher, JobDto last)
    {
        var sentinel = Next(last, "queued", 99);
        await publisher.JobChangedAsync(sentinel, Ct);
        (await ReadAsync(changes)).ShouldBe(sentinel);
    }

    private async Task ConnectAsync(string origin)
    {
        var server = host.Server;
        connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, JobsHub.Path), HttpTransportType.LongPolling, o =>
            {
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.Headers["Origin"] = origin;
            })
            .Build();
        connection.On<JobDto[]>(JobsHub.SnapshotEvent, s => snapshots.Writer.TryWrite(s));
        connection.On<JobDto>(JobsHub.ChangedEvent, j => changes.Writer.TryWrite(j));
        connection.On<ExternalReviewDto>(JobsHub.ExternalReviewChangedEvent, r => reviews.Writer.TryWrite(r));
        await connection.StartAsync(Ct);
    }

    private Task<JobDto> EnqueueAsync() =>
        WithJobsAsync(async s => (await s.EnqueueAsync(CountingJobHandler.JobType, JobQueues.Fetch, null, Ct)).Job);

    private async Task<T> WithJobsAsync<T>(Func<IJobService, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IJobService>());
    }

    private static async Task<T> ReadAsync<T>(Channel<T> channel)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(EventTimeout);
        return await channel.Reader.ReadAsync(cts.Token);
    }

    private static JobProgress Progress(long done) => new(done, 10, null);

    private static JobDto Job(string status, long done)
    {
        var now = DateTimeOffset.UtcNow;
        return new JobDto(Guid.NewGuid(), "test-synthetic", JobQueues.Fetch, status, Progress(done), null, now, now, now, null, 1);
    }

    /// <summary>The next version of <paramref name="job"/>: every job write increments <c>Version</c>.</summary>
    private static JobDto Next(JobDto job, string status, long done) =>
        job with { Status = status, Progress = Progress(done), Version = job.Version + 1 };

    /// <summary>Runs <paramref name="before"/> once, just before the runner's finish <c>UPDATE</c> (the one that sets <c>error</c>).</summary>
    private sealed class BeforeJobFinishInterceptor(Func<Task> before) : DbCommandInterceptor
    {
        private int fired;

        public bool Fired => fired == 1;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE jobs", StringComparison.Ordinal)
                && command.CommandText.Contains("error =", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref fired, 1, 0) == 0)
            {
                await before();
            }

            return result;
        }
    }

    /// <summary>A hub whose broadcasts block until <see cref="Release"/>, like a client that stopped reading.</summary>
    private sealed class StalledHubContext : IHubContext<JobsHub>, IHubClients, IClientProxy
    {
        public TaskCompletionSource FirstSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<JobDto> Sent { get; } = Channel.CreateUnbounded<JobDto>();

        IHubClients IHubContext<JobsHub>.Clients => this;
        IGroupManager IHubContext<JobsHub>.Groups => throw new NotSupportedException();
        public IClientProxy All => this;

        public async Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            FirstSendStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            Sent.Writer.TryWrite((JobDto)args[0]!);
        }

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy Group(string groupName) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }
}
