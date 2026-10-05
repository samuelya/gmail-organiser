using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

/// <summary>Sender engagement stats (#347): the rebuild's counts and kinds, its chunking, and its automatic enqueue after a fetch.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SenderStatsRebuildJobTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Human = "friend@example.com";
    private const string Bulk = "news@shop.example.com";
    private const string Mixed = "service@bank.example.com";
    private const string Me = "me@example.com";

    /// <summary>More senders than one chunk, so the rebuild checkpoints at least once.</summary>
    private const int FillerSenders = SenderStatsRebuildJob.ChunkSize + 50;

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private WebApplicationFactory<Program> host = null!;
    private JobRunner runner = null!;
    private int nextId;

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
    public async Task Rebuild_counts_live_messages_classifies_senders_and_replays_unchanged()
    {
        await SeedAsync();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

        var started = await client.PostAsync("/api/senders/stats/rebuild", null, Ct);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var job = (await started.Content.ReadFromJsonAsync<JobDto>(Ct))!;
        job.Queue.ShouldBe(JobQueues.Maintenance);
        var again = await client.PostAsync("/api/senders/stats/rebuild", null, Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await again.Content.ReadFromJsonAsync<JobDto>(Ct))!.Id.ShouldBe(job.Id);

        await RunNextAsync();
        await AssertStatsAsync(job.Id);

        // A second run (as after a crash before the last checkpoint) writes the same values.
        var second = (await (await client.PostAsync("/api/senders/stats/rebuild", null, Ct)).Content.ReadFromJsonAsync<JobDto>(Ct))!;
        await RunNextAsync();
        await AssertStatsAsync(second.Id);
    }

    [Fact]
    public async Task A_completed_mailbox_fetch_queues_a_rebuild_that_fills_the_stats()
    {
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IJobService>().EnqueueAsync(MailboxFetchJob.JobType, JobQueues.Fetch, null, Ct);
        }

        await RunNextAsync();

        await using var db = postgres.CreateDbContext();
        var rebuild = await db.Jobs.AsNoTracking().SingleAsync(j => j.Type == SenderStatsRebuildJob.JobType, Ct);
        (rebuild.Queue, rebuild.Status).ShouldBe((JobQueues.Maintenance, JobStatus.Queued));
        (await db.Senders.CountAsync(s => s.TotalCount > 0 && s.FirstSeenAt == null, Ct)).ShouldBe(0);

        await RunNextAsync();
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == rebuild.Id, Ct)).Status.ShouldBe(JobStatus.Completed);
        (await db.Senders.CountAsync(s => s.StatsAt == null, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task An_enqueue_reuses_only_a_queued_unstarted_rebuild_that_covers_its_senders()
    {
        await using var db = postgres.CreateDbContext();
        var time = TimeProvider.System;

        var scoped = await SenderStatsRebuildJob.EnqueueAsync(db, time, [Mixed, Human], Ct);
        (await SenderStatsRebuildJob.EnqueueAsync(db, time, [Human], Ct)).ShouldBe(scoped);
        var other = await SenderStatsRebuildJob.EnqueueAsync(db, time, [Bulk], Ct);
        other.ShouldNotBe(scoped);

        var full = await SenderStatsRebuildJob.EnqueueAsync(db, time, null, Ct);
        full.ShouldNotBe(scoped);
        (await SenderStatsRebuildJob.EnqueueAsync(db, time, null, Ct)).ShouldBe(full);

        // A running (or paused) rebuild may have passed the changed senders already: a follow-up queues behind it.
        await db.Jobs.Where(j => j.Id == full)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Running).SetProperty(j => j.StartedAt, time.GetUtcNow()), Ct);
        var followUp = await SenderStatsRebuildJob.EnqueueAsync(db, time, null, Ct);
        followUp.ShouldNotBe(full);
        (await SenderStatsRebuildJob.EnqueueAsync(db, time, [Bulk, Mixed], Ct)).ShouldBe(followUp);

        var cursor = (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == scoped, Ct)).Cursor!;
        JsonSerializer.Deserialize<SenderStatsRebuildCursor>(cursor, JsonSerializerOptions.Web)!.Addresses
            .ShouldBe([Human, Mixed]);
    }

    [Fact]
    public async Task A_scoped_rebuild_writes_only_its_senders()
    {
        await SeedAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await SenderStatsRebuildJob.EnqueueAsync(db, TimeProvider.System, [Human, "nobody@example.com"], Ct);
        }

        await RunNextAsync();

        await using var check = postgres.CreateDbContext();
        var rebuilt = await check.Senders.AsNoTracking().Where(s => s.StatsAt != null).Select(s => s.Address).ToListAsync(Ct);
        rebuilt.ShouldBe([Human]);
        (await check.Senders.AsNoTracking().SingleAsync(s => s.Address == Human, Ct)).Kind.ShouldBe(SenderKind.Human);
    }

    private async Task AssertStatsAsync(Guid jobId)
    {
        await using var db = postgres.CreateDbContext();
        var senderCount = FillerSenders + 4;
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId, Ct);
        job.Status.ShouldBe(JobStatus.Completed);
        job.ToDto().Progress.ShouldBe(new JobProgress(
            senderCount, senderCount, $"Senders: 2 human, 1 bulk, 1 mixed, {FillerSenders} unknown"));

        var senders = await db.Senders.AsNoTracking().ToDictionaryAsync(s => s.Address, Ct);
        var human = senders[Human];
        (human.Kind, human.RepliedCount, human.UnreadCount, human.StarredCount).ShouldBe((SenderKind.Human, 2, 1, 1));
        // Set later by an earlier chunk (Gmail lists newest first), first_seen_at moved back to the oldest message.
        human.FirstSeenAt.ShouldBe(Start.AddDays(1));

        var bulk = senders[Bulk];
        (bulk.Kind, bulk.UnreadCount, bulk.ListUnsubscribeCount, bulk.BulkHeaderCount, bulk.PromotionsCount, bulk.RepliedCount)
            .ShouldBe((SenderKind.Bulk, 100, 100, 80, 100, 0));
        // The deleted message is not counted, and first_seen_at never moves later, in the updater or the rebuild.
        bulk.StarredCount.ShouldBe(0);
        bulk.FirstSeenAt.ShouldBe(Start.AddDays(-30));

        var mixed = senders[Mixed];
        // A reply deleted in Gmail does not make the thread replied.
        (mixed.Kind, mixed.PromotionsCount, mixed.PrimaryCount, mixed.UpdatesCount, mixed.SocialCount, mixed.ForumsCount, mixed.RepliedCount)
            .ShouldBe((SenderKind.Mixed, 4, 3, 3, 0, 1, 0));
        mixed.BulkHeaderCount.ShouldBe(0);

        senders[Me].Kind.ShouldBe(SenderKind.Human);
        senders.Values.ShouldAllBe(s => s.StatsAt != null);
        senders.Values.Where(s => s.Address.StartsWith("filler", StringComparison.Ordinal))
            .ShouldAllBe(s => s.Kind == SenderKind.Unknown && s.UnreadCount == 1 && s.FirstSeenAt != null);

        // The SQL grouping agrees with BulkSignal on header fields for every seeded combination.
        var messages = await db.Messages.AsNoTracking().Where(m => m.FromAddress == Bulk && !m.DeletedInGmail).ToListAsync(Ct);
        messages.Count(m => BulkSignal.Of(new MessageRow { Precedence = m.Precedence, AutoSubmitted = m.AutoSubmitted, ListId = m.ListId }) == MessageOrigin.Bulk)
            .ShouldBe(bulk.BulkHeaderCount);
    }

    /// <summary>About 500 synthetic messages: a replied-to human, a bulk list, a mixed sender and filler senders.</summary>
    private async Task SeedAsync()
    {
        await using var db = postgres.CreateDbContext();

        // Human: one thread the user answered (SENT in the thread), one marked replied, one unread; nothing bulk.
        db.Messages.AddRange(
            Message(Human, Start.AddDays(1), MessageCategory.Primary, "t-human", [FakeLabel.Unread]),
            Message(Me, Start.AddDays(2), null, "t-human", [FakeLabel.Sent]),
            Message(Human, Start.AddDays(3), MessageCategory.Primary, null, [FakeLabel.Starred], m => m.ThreadReplied = true),
            Message(Human, Start.AddDays(4), MessageCategory.Primary, null, []));

        // Bulk: 100 unread promotions with List-Unsubscribe; 80 carry bulk headers in varied forms, 20 carry none or "no".
        string[] precedences = ["bulk", "list; x=y", "junk (spam)"];
        for (var i = 0; i < 100; i++)
        {
            db.Messages.Add(Message(Bulk, Start.AddHours(i), MessageCategory.Promotions, null, [FakeLabel.Unread], m =>
            {
                m.ListUnsubscribe = "<mailto:unsubscribe@shop.example.com>";
                if (i < 60)
                {
                    m.Precedence = precedences[i % 3];
                }
                else if (i < 70)
                {
                    m.AutoSubmitted = "auto-generated";
                }
                else if (i < 80)
                {
                    m.ListId = "<news.shop.example.com>";
                }
                else
                {
                    m.AutoSubmitted = i % 2 == 0 ? "no" : null;
                    m.ListId = i % 4 == 1 ? " " : null;
                }
            }));
        }

        db.Messages.Add(Message(Bulk, Start.AddDays(-30), MessageCategory.Primary, null, [FakeLabel.Starred], m => m.DeletedInGmail = true));

        // Mixed: 11 unread messages, 4 promotions, 3 primary, 3 updates, 1 forums.
        MessageCategory[] mixed =
        [
            .. Enumerable.Repeat(MessageCategory.Promotions, 4), .. Enumerable.Repeat(MessageCategory.Primary, 3),
            .. Enumerable.Repeat(MessageCategory.Updates, 3), MessageCategory.Forums,
        ];
        db.Messages.AddRange(mixed.Select((c, i) => Message(Mixed, Start.AddDays(i), c, i == 0 ? "t-mixed" : null, [FakeLabel.Unread])));
        db.Messages.Add(Message(Me, Start.AddDays(1), null, "t-mixed", [FakeLabel.Sent], m => m.DeletedInGmail = true));

        for (var i = 0; i < FillerSenders; i++)
        {
            var filler = string.Create(CultureInfo.InvariantCulture, $"filler{i:D4}@example.com");
            db.Messages.Add(Message(filler, Start, MessageCategory.Updates, null, [FakeLabel.Unread]));
        }

        await db.SaveChangesAsync(Ct);

        // The fetch's sender update creates the rows and sets first_seen_at; the bulk sender's was set earlier (before
        // its oldest message was deleted), which the rebuild must keep; the human's was set too late, which it lowers.
        var addresses = await db.Messages.Select(m => m.FromAddress).Distinct().ToListAsync(Ct);
        db.Senders.AddRange(Sender(Bulk, Start.AddDays(-30)), Sender(Human, Start.AddDays(4)));
        await db.SaveChangesAsync(Ct);
        await new SenderStatsUpdater(db, TimeProvider.System).UpdateAsync(addresses, Ct);
    }

    private static SenderRow Sender(string address, DateTimeOffset firstSeen) => new()
    {
        Address = address,
        Domain = address.Split('@')[1],
        CanonicalAddress = address,
        CanonicalDomain = address.Split('@')[1],
        FirstSeenAt = firstSeen,
        UpdatedAt = Start,
    };

    private MessageRow Message(
        string from, DateTimeOffset date, MessageCategory? category, string? thread, string[] labels, Action<MessageRow>? configure = null)
    {
        var id = string.Create(CultureInfo.InvariantCulture, $"m{nextId++:D5}");
        var m = new MessageRow
        {
            Id = id,
            ThreadId = thread ?? id,
            FromAddress = from,
            CanonicalAddress = from,
            CanonicalDomain = from.Split('@')[1],
            InternalDate = date,
            LabelIds = labels,
            Category = category,
            FetchedAt = Start,
            UpdatedAt = Start,
        };
        configure?.Invoke(m);
        return m;
    }

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

    private static class FakeLabel
    {
        public const string Unread = "UNREAD";
        public const string Starred = "STARRED";
        public const string Sent = "SENT";
    }
}
