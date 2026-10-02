using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class SenderFetchJobTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Alice = "alice@d1.example.com";
    private const string Bob = "bob@d1.example.com";
    private const string Carol = "carol@d2.example.com";
    private const string Dave = "dave@d4.example.com";
    private const int AliceCount = 150;
    private const int BobCount = 60;
    private const int CarolCount = 30;

    // More than one Gmail page (500), so a chunk of 1000 spans two pages.
    private const int DaveCount = 600;
    private const int ChunkSize = SettingsValidation.MinFetchChunkSize;
    private static readonly DateTimeOffset Newest = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ConcurrentQueue<JobDto> published = new();
    private WebApplicationFactory<Program> host = null!;
    private CountingGmailClient gmail = null!;
    private JobRunner runner = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Jobs.ExecuteDeleteAsync();
            await db.Messages.ExecuteDeleteAsync();
            await db.Senders.ExecuteDeleteAsync();
            await db.Settings.ExecuteDeleteAsync();
            await db.FetchState.ExecuteUpdateAsync(s => s.SetProperty(r => r.AccountEmail, (string?)null));
        }

        host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true").ConfigureTestServices(services =>
        {
            services.AddSingleton(sp => new FakeGmailClient(sp.GetRequiredService<FakeTokenStore>(), Seed()));
            services.AddSingleton(sp => new CountingGmailClient(sp.GetRequiredService<FakeGmailClient>()));
            services.AddScoped<IGmailClient>(sp => sp.GetRequiredService<CountingGmailClient>());
            services.AddSingleton<IJobProgressPublisher>(new RecordingPublisher(published));
            services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner)));
        }));
        runner = ActivatorUtilities.CreateInstance<JobRunner>(host.Services);
        gmail = host.Services.GetRequiredService<CountingGmailClient>();
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(x => x with { FetchChunkSize = ChunkSize }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Address_target_fetches_exactly_that_senders_messages_in_checkpointed_chunks()
    {
        var response = await PostAsync(host, " Alice@D1.Example.com ");
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var jobId = (await response.Content.ReadFromJsonAsync<StartFetchResponse>(Ct)).ShouldNotBeNull().JobId;

        await RunNextAsync(jobId);

        await using var db = postgres.CreateDbContext();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct);
        job.Status.ShouldBe(JobStatus.Completed);
        var cursor = JsonDocument.Parse(job.Cursor.ShouldNotBeNull()).RootElement;
        cursor.GetProperty("target").GetString().ShouldBe(Alice);
        cursor.GetProperty("kind").GetString().ShouldBe("address");
        cursor.GetProperty("fetched").GetInt32().ShouldBe(AliceCount);
        (await db.Messages.Select(m => m.FromAddress).Distinct().ToListAsync(Ct)).ShouldBe([Alice]);
        (await db.Messages.CountAsync(Ct)).ShouldBe(AliceCount);
        (await db.Senders.SingleAsync(Ct)).TotalCount.ShouldBe(AliceCount);
        (await db.FetchState.SingleAsync(Ct)).AccountEmail.ShouldBe(FakeGmailClient.AccountEmail);

        // The first checkpoint carries the first page's estimate as the total.
        var progress = published.Where(j => j.Id == jobId && j.Progress is not null).Select(j => j.Progress!).Distinct().ToList();
        progress.ShouldContain(new JobProgress(ChunkSize, AliceCount, "Fetching sender"));
        progress[^1].ShouldBe(new JobProgress(AliceCount, AliceCount, "Fetching sender"));
    }

    [Fact]
    public async Task Domain_target_fetches_every_sender_of_that_domain_only()
    {
        var jobId = await StartAsync("d1.example.com");

        await RunNextAsync(jobId);

        await using var db = postgres.CreateDbContext();
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct)).Status.ShouldBe(JobStatus.Completed);
        (await db.Messages.CountAsync(Ct)).ShouldBe(AliceCount + BobCount);
        var senders = await db.Senders.OrderBy(s => s.Address).ToDictionaryAsync(s => s.Address, s => s.TotalCount, Ct);
        senders.ShouldBe(new Dictionary<string, int> { [Alice] = AliceCount, [Bob] = BobCount });
    }

    [Fact]
    public async Task Target_without_messages_completes_with_zero_done()
    {
        var jobId = await StartAsync("nobody@d3.example.com");

        await RunNextAsync(jobId);

        await using var db = postgres.CreateDbContext();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct);
        job.Status.ShouldBe(JobStatus.Completed);
        job.ToDto().Progress.ShouldBe(new JobProgress(0, 0, "Fetching sender"));
        (await db.Messages.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Total_is_the_first_pages_estimate_even_when_a_chunk_spans_two_pages()
    {
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(x => x with { FetchChunkSize = 1000 }, Ct);
        }

        // Gmail's estimate drifts between pages; the cursor must keep page one's.
        gmail.MapPage = (call, page) => page with { ResultSizeEstimate = 1000 + call };
        var jobId = await StartAsync("@d4.example.com");

        await RunNextAsync(jobId);

        gmail.ListCalls.Count.ShouldBe(2);
        await using var db = postgres.CreateDbContext();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct);
        job.Status.ShouldBe(JobStatus.Completed);
        var cursor = JsonDocument.Parse(job.Cursor.ShouldNotBeNull()).RootElement;
        cursor.GetProperty("total").GetInt64().ShouldBe(1001);
        cursor.GetProperty("fetched").GetInt32().ShouldBe(DaveCount);
        cursor.TryGetProperty("query", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Same_target_twice_returns_the_existing_job_and_another_target_queues_its_own()
    {
        var first = await PostAsync(host, Carol);
        var second = await PostAsync(host, "CAROL@d2.example.com");
        var other = await PostAsync(host, Bob);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        first.Headers.Location?.OriginalString.ShouldStartWith("/api/jobs/");
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstId = (await first.Content.ReadFromJsonAsync<StartFetchResponse>(Ct)).ShouldNotBeNull().JobId;
        (await second.Content.ReadFromJsonAsync<StartFetchResponse>(Ct)).ShouldNotBeNull().JobId.ShouldBe(firstId);
        other.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var otherId = (await other.Content.ReadFromJsonAsync<StartFetchResponse>(Ct)).ShouldNotBeNull().JobId;
        otherId.ShouldNotBe(firstId);

        // Both run, one after the other, on the fetch queue.
        await RunNextAsync(firstId);
        await RunNextAsync(otherId);
        await using var db = postgres.CreateDbContext();
        var jobs = await db.Jobs.AsNoTracking().OrderBy(j => j.CreatedAt).ToListAsync(Ct);
        jobs.Select(j => (j.DedupKey, j.Status)).ShouldBe([(Carol, JobStatus.Completed), (Bob, JobStatus.Completed)]);
        (await db.Messages.CountAsync(Ct)).ShouldBe(CarolCount + BobCount);
    }

    [Theory]
    [InlineData(JobStatus.Paused)]
    [InlineData(JobStatus.Failed)]
    public async Task Same_target_with_a_paused_or_failed_job_resumes_it_from_its_checkpoint(JobStatus status)
    {
        var jobId = await StartAsync(Alice);
        const string checkpoint = """{"target":"alice@d1.example.com","kind":"address","pageToken":"100","fetched":100,"total":150}""";
        await using (var db = postgres.CreateDbContext())
        {
            await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, status).SetProperty(j => j.Cursor, checkpoint), Ct);
        }

        var again = await PostAsync(host, Alice);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<StartFetchResponse>(Ct)).ShouldNotBeNull().JobId.ShouldBe(jobId);
        await using var check = postgres.CreateDbContext();
        var job = (await check.Jobs.AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem();
        job.Status.ShouldBe(JobStatus.Queued);
        JsonDocument.Parse(job.Cursor.ShouldNotBeNull()).RootElement.GetProperty("pageToken").GetString().ShouldBe("100");
    }

    [Fact]
    public async Task A_finished_job_for_the_target_does_not_block_a_new_one()
    {
        var firstId = await StartAsync(Carol);
        await RunNextAsync(firstId);

        var again = await PostAsync(host, Carol);

        again.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await again.Content.ReadFromJsonAsync<StartFetchResponse>(Ct)).ShouldNotBeNull().JobId.ShouldNotBe(firstId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@@d1.example.com")]
    [InlineData("@alice@d1.example.com")]
    [InlineData("com")]
    [InlineData("@com")]
    [InlineData("alice@localhost")]
    [InlineData("alice@")]
    [InlineData("alice@@d1.example.com")]
    [InlineData("alice@d1.example.com OR from:bob")]
    [InlineData("d1.example.com OR in:trash")]
    [InlineData("\"alice\"@d1.example.com")]
    [InlineData("{alice bob}@d1.example.com")]
    [InlineData("(alice)@d1.example.com")]
    [InlineData(".d1.example.com")]
    [InlineData("d1.example.com.")]
    [InlineData("d1..example.com")]
    [InlineData("-d1.example.com")]
    [InlineData("d1_example.com")]
    [InlineData("alice.@d1.example.com")]
    [InlineData("in:anywhere")]
    [InlineData("exämple.com")]
    public async Task Invalid_targets_are_a_400_problem_and_enqueue_nothing(string? target)
    {
        var response = await PostAsync(host, target);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct)).ShouldNotBeNull().Title.ShouldBe("Invalid sender");
        await using var db = postgres.CreateDbContext();
        (await db.Jobs.CountAsync(Ct)).ShouldBe(0);
    }

    [Theory]
    [InlineData("first.last+tag@sub.d1.example.com", "first.last+tag@sub.d1.example.com", "address")]
    [InlineData("o'brien@example.com", "o'brien@example.com", "address")]
    [InlineData("xn--bcher-kva.example.com", "xn--bcher-kva.example.com", "domain")]
    [InlineData(" @D1.Example.com", "d1.example.com", "domain")]
    public async Task Valid_targets_are_accepted_with_their_kind(string input, string target, string kind)
    {
        var jobId = await StartAsync(input);

        await using var db = postgres.CreateDbContext();
        var job = await db.Jobs.SingleAsync(j => j.Id == jobId, Ct);
        job.DedupKey.ShouldBe(target);
        var cursor = JsonDocument.Parse(job.Cursor.ShouldNotBeNull()).RootElement;
        cursor.GetProperty("target").GetString().ShouldBe(target);
        cursor.GetProperty("kind").GetString().ShouldBe(kind);
    }

    [Fact]
    public async Task Start_without_a_Gmail_connection_is_a_409_problem()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.OAuthTokens.ExecuteDeleteAsync(Ct);
        }

        await using var disconnected = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner)))));

        var response = await PostAsync(disconnected, Alice);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct)).ShouldNotBeNull().Title.ShouldBe("Gmail not connected");
        await using var check = postgres.CreateDbContext();
        (await check.Jobs.CountAsync(Ct)).ShouldBe(0);
    }

    private async Task<Guid> StartAsync(string target)
    {
        var response = await PostAsync(host, target);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<StartFetchResponse>(Ct)).ShouldNotBeNull().JobId;
    }

    private async Task RunNextAsync(Guid expected)
    {
        var id = (await runner.ClaimAsync(Ct)).ShouldHaveSingleItem();
        id.ShouldBe(expected);
        await runner.RunAsync(id, Ct);
    }

    private static Task<HttpResponseMessage> PostAsync(WebApplicationFactory<Program> app, string? target)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client.PostAsJsonAsync("/api/fetch/sender", new SenderFetchRequest(target), Ct);
    }

    /// <summary>Two senders sharing <c>d1.example.com</c> and one at <c>d2.example.com</c>, interleaved by date; one large sender at <c>d4.example.com</c>.</summary>
    private static List<FakeMessage> Seed() =>
    [
        .. Enumerable.Range(0, AliceCount).Select(i => Message($"a{i:D4}", $"Alice <{Alice}>", i * 3)),
        .. Enumerable.Range(0, BobCount).Select(i => Message($"b{i:D4}", Bob, (i * 3) + 1)),
        .. Enumerable.Range(0, CarolCount).Select(i => Message($"c{i:D4}", $"Carol <{Carol}>", (i * 3) + 2)),
        .. Enumerable.Range(0, DaveCount).Select(i => Message($"d{i:D4}", Dave, 1000 + i)),
    ];

    private static FakeMessage Message(string id, string from, int minutesAgo) =>
        new(id, $"t-{id}", from, $"Synthetic subject {id}", Newest.AddMinutes(-minutesAgo), ["INBOX"]);

    private sealed class RecordingPublisher(ConcurrentQueue<JobDto> published) : IJobProgressPublisher
    {
        public Task JobChangedAsync(JobDto job, CancellationToken ct)
        {
            published.Enqueue(job);
            return Task.CompletedTask;
        }
    }
}
