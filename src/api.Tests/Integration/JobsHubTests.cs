using System.Net;
using System.Threading.Channels;
using GmailOrganiser.Jobs;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
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
        host = CreateFakeClockHost();
        await ConnectAsync(ApiFactory.AllowedOrigin);
        await ReadAsync(snapshots);
        var publisher = host.Services.GetRequiredService<IJobProgressPublisher>();
        var job = Job("running", 0);

        await publisher.JobChangedAsync(job, Ct);
        await publisher.JobChangedAsync(job with { Progress = Progress(1) }, Ct);
        await publisher.JobChangedAsync(job with { Progress = Progress(2) }, Ct);
        (await ReadAsync(changes)).Progress.ShouldBe(Progress(0));
        changes.Reader.TryRead(out _).ShouldBeFalse();

        clock.Advance(TimeSpan.FromSeconds(1));
        (await ReadAsync(changes)).Progress.ShouldBe(Progress(2));

        clock.Advance(TimeSpan.FromSeconds(1));
        await publisher.JobChangedAsync(job with { Progress = Progress(3) }, Ct);
        (await ReadAsync(changes)).Progress.ShouldBe(Progress(3));
    }

    [Fact]
    public async Task A_status_change_is_sent_at_once_and_drops_parked_progress()
    {
        host = CreateFakeClockHost();
        await ConnectAsync(ApiFactory.AllowedOrigin);
        await ReadAsync(snapshots);
        var publisher = host.Services.GetRequiredService<IJobProgressPublisher>();
        var job = Job("running", 0);

        await publisher.JobChangedAsync(job, Ct);
        await publisher.JobChangedAsync(job with { Progress = Progress(1) }, Ct);
        await publisher.JobChangedAsync(job with { Status = "completed", Progress = Progress(2) }, Ct);

        (await ReadAsync(changes)).Progress.ShouldBe(Progress(0));
        var done = await ReadAsync(changes);
        done.Status.ShouldBe("completed");
        done.Progress.ShouldBe(Progress(2));

        clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        changes.Reader.TryRead(out _).ShouldBeFalse();
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

    private WebApplicationFactory<Program> CreateFakeClockHost() =>
        CountingJobHandler.CreateHost(factory, state)
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));

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
        await connection.StartAsync(Ct);
    }

    private Task<JobDto> EnqueueAsync() =>
        WithJobsAsync(s => s.EnqueueAsync(CountingJobHandler.JobType, JobQueues.Fetch, null, Ct));

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
        return new JobDto(Guid.NewGuid(), "test-synthetic", JobQueues.Fetch, status, Progress(done), null, now, now, now, null);
    }
}
