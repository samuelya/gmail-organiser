using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Senders;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Canonical sender (#345): the fetch path decodes relay senders, the backfill decodes rows stored before it.</summary>
[Collection(PostgresCollection.Name)]
public sealed class CanonicalBackfillJobTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Shop = "offers@shop.example.com";
    private const string Opaque = "ab12cd34ef@privaterelay.appleid.com";

    /// <summary>More relay rows than one chunk, so the backfill checkpoints at least once.</summary>
    private const int RelayCount = CanonicalBackfillJob.ChunkSize + 5;

    private static readonly DateTimeOffset Newest = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private WebApplicationFactory<Program> host = null!;
    private JobRunner runner = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await CleanAsync();
        host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<IGmailClient>(new FakeGmailClient(new FakeTokenStore(TimeProvider.System), TimeProvider.System));
            services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner)));
        }));
        runner = ActivatorUtilities.CreateInstance<JobRunner>(host.Services);
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await CleanAsync();
        await postgres.ResetFetchStateAsync();
    }

    [Fact]
    public async Task Fetch_stores_the_seed_relay_senders_under_the_shop_canonical_sender()
    {
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IJobService>().EnqueueAsync(MailboxFetchJob.JobType, JobQueues.Fetch, null, Ct);
        }

        await RunNextAsync();

        await using var db = postgres.CreateDbContext();
        var shop = await db.Senders.Where(s => s.CanonicalAddress == Shop).OrderBy(s => s.Address).ToListAsync(Ct);
        shop.Select(s => s.Address).ShouldBe([.. FakeMailboxSeed.RelayAddresses.Append(Shop).Order(StringComparer.Ordinal)]);
        shop.ShouldAllBe(s => s.CanonicalDomain == "shop.example.com");
        shop.Count(s => s.IsRelay).ShouldBe(FakeMailboxSeed.RelayAddresses.Length);
        (await db.Messages.CountAsync(m => FakeMailboxSeed.RelayAddresses.Contains(m.FromAddress) && m.CanonicalAddress == Shop, Ct))
            .ShouldBe(FakeMailboxSeed.RelayAddresses.Length);
        (await db.Messages.CountAsync(m => m.CanonicalAddress != m.FromAddress && !FakeMailboxSeed.RelayAddresses.Contains(m.FromAddress), Ct)).ShouldBe(0);

        var page = await host.CreateClient().GetFromJsonAsync<PagedDto<SenderDto>>("/api/senders?search=shop.example", Ct);
        page!.Items.Select(s => s.Address).ShouldBe(shop.Select(s => s.Address), ignoreOrder: true);
    }

    [Fact]
    public async Task Backfill_decodes_only_relay_rows_reports_counts_refuses_a_second_run_and_replays_unchanged()
    {
        await SeedUndecodedAsync();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

        var started = await client.PostAsync("/api/senders/canonical/backfill", null, Ct);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var job = (await started.Content.ReadFromJsonAsync<JobDto>(Ct))!;
        job.Queue.ShouldBe(JobQueues.Senders);
        (await client.PostAsync("/api/senders/canonical/backfill", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await RunNextAsync();
        await AssertDecodedAsync(job.Id);

        // A second run (as after a crash before the last checkpoint) changes nothing.
        var again = await client.PostAsync("/api/senders/canonical/backfill", null, Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await RunNextAsync();
        await AssertDecodedAsync((await again.Content.ReadFromJsonAsync<JobDto>(Ct))!.Id);
    }

    private async Task AssertDecodedAsync(Guid jobId)
    {
        await using var db = postgres.CreateDbContext();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct);
        job.Status.ShouldBe(JobStatus.Completed);
        job.ToDto().Progress.ShouldBe(new JobProgress(RelayCount + 1, RelayCount + 1, $"Relay messages: {RelayCount} decoded, 1 undecodable"));

        (await db.Messages.CountAsync(m => m.Id.StartsWith("r") && m.CanonicalAddress == Shop && m.CanonicalDomain == "shop.example.com", Ct))
            .ShouldBe(RelayCount);
        (await db.Messages.SingleAsync(m => m.FromAddress == Opaque, Ct)).CanonicalAddress.ShouldBe(Opaque);
        // Non-relay rows are not read: the migration already set them.
        (await db.Messages.SingleAsync(m => m.FromAddress == Shop, Ct)).CanonicalAddress.ShouldBe("");

        var senders = await db.Senders.AsNoTracking().ToDictionaryAsync(s => s.Address, Ct);
        (senders[FakeMailboxSeed.RelayAddresses[0]].CanonicalAddress, senders[FakeMailboxSeed.RelayAddresses[0]].IsRelay).ShouldBe((Shop, true));
        (senders[Opaque].CanonicalAddress, senders[Opaque].IsRelay).ShouldBe((Opaque, false));
        senders[Shop].CanonicalAddress.ShouldBe("");
    }

    /// <summary>Rows as stored before the decoder: relay senders carry their raw address as canonical.</summary>
    private async Task SeedUndecodedAsync()
    {
        await using var db = postgres.CreateDbContext();
        var relay = FakeMailboxSeed.RelayAddresses[0];
        db.Messages.AddRange(Enumerable.Range(0, RelayCount).Select(i => Message(string.Create(CultureInfo.InvariantCulture, $"r{i:D5}"), relay, relay)));
        db.Messages.AddRange(Message("o00001", Opaque, Opaque), Message("p00001", Shop, ""));
        db.Senders.AddRange(Sender(relay, relay), Sender(Opaque, Opaque), Sender(Shop, ""));
        await db.SaveChangesAsync(Ct);
    }

    private static MessageRow Message(string id, string from, string canonical) => new()
    {
        Id = id,
        ThreadId = id,
        FromAddress = from,
        CanonicalAddress = canonical,
        CanonicalDomain = canonical.Split('@').Last(),
        InternalDate = Newest,
        FetchedAt = Newest,
        UpdatedAt = Newest,
    };

    private static SenderRow Sender(string address, string canonical) => new()
    {
        Address = address,
        Domain = address.Split('@')[1],
        CanonicalAddress = canonical,
        CanonicalDomain = canonical.Split('@').Last(),
        UpdatedAt = Newest,
    };

    private async Task CleanAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Jobs.ExecuteDeleteAsync(Ct);
        await db.FetchRunMessages.ExecuteDeleteAsync(Ct);
        await db.Messages.ExecuteDeleteAsync(Ct);
        await db.Senders.ExecuteDeleteAsync(Ct);
    }

    private async Task RunNextAsync()
    {
        var id = (await runner.ClaimAsync(Ct)).ShouldHaveSingleItem();
        await runner.RunAsync(id, Ct);
    }
}
