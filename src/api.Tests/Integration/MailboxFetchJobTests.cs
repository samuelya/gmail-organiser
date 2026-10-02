using System.Collections.Concurrent;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class MailboxFetchJobTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const int MessageCount = 2300;
    private const int InboxCount = 900;
    private const int SenderCount = 10;
    private const int SpamCount = 20;
    private const int TrashCount = 10;
    private static readonly DateTimeOffset Newest = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ConcurrentQueue<JobDto> published = new();
    private CountingGmailClient gmail = null!;
    private WebApplicationFactory<Program> host = null!;
    private JobRunner runner = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Jobs.ExecuteDeleteAsync();
            await db.FetchRunMessages.ExecuteDeleteAsync();
            await db.Messages.ExecuteDeleteAsync();
            await db.Senders.ExecuteDeleteAsync();
            await db.Settings.ExecuteDeleteAsync();
        }

        gmail = new CountingGmailClient(new FakeGmailClient(new FakeTokenStore(TimeProvider.System), Seed()));
        host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<IGmailClient>(gmail);
            services.AddSingleton<IJobProgressPublisher>(new RecordingPublisher(published));
            services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner)));
        }));
        runner = ActivatorUtilities.CreateInstance<JobRunner>(host.Services);
        await WithAsync<ISettingsStore, AppSettings>(s => s.UpdateAsync(x => x with { FetchChunkSize = 1000 }, Ct));
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Full_run_stores_every_message_once_inbox_first_and_checkpoints_each_chunk()
    {
        gmail.AfterMetadata = _ =>
        {
            gmail.Inner.AdvanceHistoryId(50);
            return Task.CompletedTask;
        };
        var job = await EnqueueAsync();

        await RunNextAsync();

        (await GetJobAsync(job.Id)).Status.ShouldBe("completed");
        await using var db = postgres.CreateDbContext();
        (await db.Messages.CountAsync(Ct)).ShouldBe(MessageCount);

        var inboxIds = InboxIds();
        gmail.ListCalls.Take(2).ShouldAllBe(q => q.LabelIds != null && q.LabelIds.SequenceEqual(new[] { "INBOX" }));
        gmail.ListCalls.Skip(2).ShouldAllBe(q => q.LabelIds == null);
        gmail.MetadataCalls.First().ShouldBe(inboxIds, ignoreOrder: true);
        AssertEachMessageFetchedOnce();

        // One checkpoint for the start, then one per chunk; Spam and Trash are not part of the All Mail total.
        published.Count(j => j.Status == "running" && j.Progress != null).ShouldBe(5);
        published.Single(j => j.Progress is { Message: "Fetching Inbox" }).Progress.ShouldBe(new JobProgress(InboxCount, InboxCount, "Fetching Inbox"));
        published.Last(j => j.Status == "running").Progress.ShouldBe(new JobProgress(MessageCount, MessageCount, "Fetching All Mail"));
        (await db.FetchRunMessages.CountAsync(Ct)).ShouldBe(0);

        var state = await db.FetchState.SingleAsync(Ct);
        state.MailboxPhase.ShouldBe(MailboxPhase.Completed);
        state.PageToken.ShouldBeNull();
        state.CompletedAt.ShouldNotBeNull();
        state.AccountEmail.ShouldBe(FakeGmailClient.AccountEmail);
        state.MessagesTotal.ShouldBe(MessageCount + SpamCount + TrashCount);
        state.InboxFetched.ShouldBe(InboxCount);
        state.AllMailFetched.ShouldBe(MessageCount);
        state.LastHistoryId.ShouldBe(FakeMailboxSeed.HistoryId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        (await gmail.GetProfileAsync(Ct)).HistoryId.ShouldNotBe(state.LastHistoryId);

        var named = await db.Senders.SingleAsync(s => s.Address == "sender0@example.com", Ct);
        named.Domain.ShouldBe("example.com");
        named.TotalCount.ShouldBe(MessageCount / SenderCount);
        named.LastSeenAt.ShouldBe(Newest);
        named.DisplayName.ShouldBe("New Name");
        var bare = await db.Senders.SingleAsync(s => s.Address == "sender1@example.com", Ct);
        bare.TotalCount.ShouldBe(MessageCount / SenderCount);
        bare.LastSeenAt.ShouldBe(Newest.AddMinutes(-1));
        bare.DisplayName.ShouldBeNull();
        (await db.Senders.CountAsync(Ct)).ShouldBe(SenderCount);
    }

    [Fact]
    public async Task Pause_after_the_first_chunk_then_resume_does_not_refetch_it()
    {
        var job = await EnqueueAsync();
        gmail.AfterMetadata = call => call == 1 ? WithAsync<IJobService, JobActionResult>(s => s.PauseAsync(job.Id, Ct)) : Task.CompletedTask;

        await RunNextAsync();

        (await GetJobAsync(job.Id)).Status.ShouldBe("paused");
        gmail.MetadataCalls.Count.ShouldBe(1);

        gmail.AfterMetadata = null;
        (await WithAsync<IJobService, JobActionResult>(s => s.ResumeAsync(job.Id, Ct))).ShouldBe(JobActionResult.Ok);
        await RunNextAsync();

        (await GetJobAsync(job.Id)).Status.ShouldBe("completed");
        gmail.ListCalls.Count(q => q.LabelIds != null).ShouldBe(2);
        AssertEachMessageFetchedOnce();
        await using var db = postgres.CreateDbContext();
        (await db.Messages.CountAsync(Ct)).ShouldBe(MessageCount);
    }

    [Fact]
    public async Task Second_full_run_keeps_analysis_status_and_fetched_at()
    {
        await EnqueueAsync();
        await RunNextAsync();
        await using var db = postgres.CreateDbContext();
        var analysed = await db.Messages.OrderBy(m => m.Id).Select(m => m.Id).Take(50).ToListAsync(Ct);
        await db.Messages.Where(m => analysed.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.AnalysisStatus, AnalysisStatus.Analysed), Ct);
        var fetchedAt = await db.Messages.ToDictionaryAsync(m => m.Id, m => m.FetchedAt, Ct);
        var updatedAt = await db.Messages.ToDictionaryAsync(m => m.Id, m => m.UpdatedAt, Ct);
        await db.Senders.ExecuteUpdateAsync(s => s.SetProperty(x => x.AnalysedCount, 7), Ct);

        var second = await EnqueueAsync();
        await RunNextAsync();

        (await GetJobAsync(second.Id)).Status.ShouldBe("completed");
        db.ChangeTracker.Clear();
        (await db.Messages.CountAsync(Ct)).ShouldBe(MessageCount);
        (await db.Messages.CountAsync(m => m.AnalysisStatus == AnalysisStatus.Analysed, Ct)).ShouldBe(analysed.Count);
        (await db.Messages.ToDictionaryAsync(m => m.Id, m => m.FetchedAt, Ct)).ShouldBe(fetchedAt, ignoreOrder: true);
        (await db.Messages.ToDictionaryAsync(m => m.Id, m => m.UpdatedAt, Ct)).ShouldBe(updatedAt, ignoreOrder: true);
        (await db.Senders.SingleAsync(s => s.Address == "sender0@example.com", Ct)).TotalCount.ShouldBe(MessageCount / SenderCount);
        (await db.Senders.AllAsync(s => s.AnalysedCount == 7, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Rejected_page_token_restarts_the_phase_from_its_first_page()
    {
        var job = await EnqueueAsync();
        gmail.AfterMetadata = call => call == 2 ? WithAsync<IJobService, JobActionResult>(s => s.PauseAsync(job.Id, Ct)) : Task.CompletedTask;
        await RunNextAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE jobs SET cursor = jsonb_set(cursor, '{{pageToken}}', '\"expired-token\"') WHERE id = {job.Id}", Ct);
        }

        gmail.AfterMetadata = null;
        (await WithAsync<IJobService, JobActionResult>(s => s.ResumeAsync(job.Id, Ct))).ShouldBe(JobActionResult.Ok);
        await RunNextAsync();

        (await GetJobAsync(job.Id)).Status.ShouldBe("completed");
        gmail.ListCalls.ShouldContain(q => q.PageToken == "expired-token");
        gmail.ListCalls.Count(q => q.LabelIds == null && q.PageToken == null).ShouldBe(2);
        await using var check = postgres.CreateDbContext();
        (await check.Messages.CountAsync(Ct)).ShouldBe(MessageCount);
        (await check.FetchState.SingleAsync(Ct)).AllMailFetched.ShouldBe(MessageCount);
    }

    [Fact]
    public async Task Rejected_token_within_a_chunk_retries_that_chunk_without_restarting_the_phase()
    {
        // List calls: Inbox 1-2, All Mail chunk 1 = 3-4, chunk 2 = 5 (the stored token) and 6 (issued within the chunk).
        gmail.RejectPageToken = (call, _) => call == 6;
        var job = await EnqueueAsync();

        await RunNextAsync();

        (await GetJobAsync(job.Id)).Status.ShouldBe("completed");
        var calls = gmail.ListCalls.ToList();
        calls[6].PageToken.ShouldBe(calls[4].PageToken);
        calls.Count(q => q.LabelIds == null && q.PageToken == null).ShouldBe(1);
        await using var db = postgres.CreateDbContext();
        (await db.Messages.CountAsync(Ct)).ShouldBe(MessageCount);
        (await db.FetchState.SingleAsync(Ct)).AllMailFetched.ShouldBe(MessageCount);
    }

    [Fact]
    public async Task Cancel_during_the_last_chunk_still_ends_completed()
    {
        var job = await EnqueueAsync();
        gmail.AfterMetadata = call => call == 4 ? WithAsync<IJobService, JobActionResult>(s => s.CancelAsync(job.Id, Ct)) : Task.CompletedTask;

        await RunNextAsync();

        gmail.MetadataCalls.Count.ShouldBe(4);
        (await GetJobAsync(job.Id)).Status.ShouldBe("completed");
        await using var db = postgres.CreateDbContext();
        (await db.FetchState.SingleAsync(Ct)).MailboxPhase.ShouldBe(MailboxPhase.Completed);
    }

    [Fact]
    public async Task A_failed_job_completion_leaves_the_fetch_at_its_last_checkpoint()
    {
        var job = await EnqueueAsync();
        await using var db = postgres.CreateDbContext();
        await db.FetchState.ExecuteUpdateAsync(s => s.SetProperty(f => f.LastHistoryId, (string?)null), Ct);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION test_fail_completion() RETURNS trigger LANGUAGE plpgsql AS
            $$ BEGIN RAISE EXCEPTION 'synthetic completion failure'; END $$;
            CREATE TRIGGER test_fail_completion BEFORE UPDATE ON jobs FOR EACH ROW
            WHEN (NEW.cursor->>'phase' = 'Completed') EXECUTE FUNCTION test_fail_completion();
            """, Ct);
        try
        {
            await RunNextAsync();
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER test_fail_completion ON jobs; DROP FUNCTION test_fail_completion();", Ct);
        }

        (await GetJobAsync(job.Id)).Status.ShouldBe("failed");
        var state = await db.FetchState.AsNoTracking().SingleAsync(Ct);
        state.MailboxPhase.ShouldBe(MailboxPhase.AllMail);
        state.CompletedAt.ShouldBeNull();
        state.LastHistoryId.ShouldBeNull();
        (await db.FetchRunMessages.CountAsync(Ct)).ShouldBe(InboxCount);

        // Re-running from the stored cursor repeats only the last chunk and still skips the Inbox ids.
        await db.Database.ExecuteSqlAsync($"UPDATE jobs SET status = 'queued', error = NULL, finished_at = NULL WHERE id = {job.Id}", Ct);
        await RunNextAsync();

        (await GetJobAsync(job.Id)).Status.ShouldBe("completed");
        (await db.FetchState.AsNoTracking().SingleAsync(Ct)).CompletedAt.ShouldNotBeNull();
        var inboxIds = InboxIds();
        gmail.MetadataCalls.SelectMany(c => c).Where(inboxIds.Contains).ShouldBeUnique();
    }

    /// <summary>All Mail skips the Inbox messages this run already stored, also across a pause.</summary>
    private void AssertEachMessageFetchedOnce()
    {
        var fetched = gmail.MetadataCalls.SelectMany(c => c).ToList();
        fetched.Count.ShouldBe(MessageCount);
        fetched.ShouldBeUnique();
    }

    /// <summary>
    /// Newest first, one minute apart; sender <c>i % 10</c>; 900 interleaved Inbox messages. <c>sender0</c> changed its
    /// display name for its newest mail, <c>sender1</c> sends without a name.
    /// </summary>
    private static List<FakeMessage> Seed() =>
    [
        .. Enumerable.Range(0, MessageCount).Select(i =>
        {
            var sender = i % SenderCount;
            var address = $"sender{sender}@example.com";
            var from = sender switch
            {
                0 => i < 100 ? $"New Name <{address}>" : $"Old Name <{address}>",
                1 => address,
                _ => $"Sender {sender} <{address}>",
            };
            string[] labels = IsInbox(i) ? ["INBOX", "CATEGORY_UPDATES"] : ["CATEGORY_PROMOTIONS"];
            return new FakeMessage($"m{i:D5}", $"t{i:D5}", from, $"Synthetic subject {i}", Newest.AddMinutes(-i), labels);
        }),
        .. Enumerable.Range(0, SpamCount + TrashCount).Select(i => new FakeMessage(
            $"x{i:D5}", $"x{i:D5}", "junk@example.com", $"Synthetic junk {i}", Newest.AddMinutes(-i), [i < SpamCount ? "SPAM" : "TRASH"])),
    ];

    private static bool IsInbox(int i) => i % 23 < 9;

    private static HashSet<string> InboxIds() =>
        [.. Enumerable.Range(0, MessageCount).Where(IsInbox).Select(i => $"m{i:D5}")];

    private Task<JobDto> EnqueueAsync() =>
        WithAsync<IJobService, JobDto>(async s => (await s.EnqueueAsync(MailboxFetchJob.JobType, MailboxFetchJob.Queue, null, Ct)).Job);

    private async Task<JobDto> GetJobAsync(Guid id) =>
        (await WithAsync<IJobService, JobDto?>(s => s.GetAsync(id, Ct))).ShouldNotBeNull();

    private async Task<TResult> WithAsync<TService, TResult>(Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private async Task RunNextAsync()
    {
        var id = (await runner.ClaimAsync(Ct)).ShouldHaveSingleItem();
        await runner.RunAsync(id, Ct);
    }

    private sealed class RecordingPublisher(ConcurrentQueue<JobDto> published) : IJobProgressPublisher
    {
        public Task JobChangedAsync(JobDto job, CancellationToken ct)
        {
            published.Enqueue(job);
            return Task.CompletedTask;
        }
    }
}
