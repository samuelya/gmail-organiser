using System.Globalization;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class IncrementalFetchJobTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const int MessageCount = 40;
    private static readonly DateTimeOffset Newest = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

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
            services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner)));
        }));
        runner = ActivatorUtilities.CreateInstance<JobRunner>(host.Services);

        await EnqueueAsync(MailboxFetchJob.JobType);
        await RunNextAsync();
        gmail.MetadataCalls.Clear();
    }

    /// <summary>Leaves fetch_state as migrated: later classes in the collection start without a local account.</summary>
    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await postgres.ResetFetchStateAsync();
    }

    [Fact]
    public async Task Run_stores_added_mail_updates_removed_labels_marks_deletions_and_advances_the_history_id()
    {
        gmail.Inner.AddMessage(NewMessage("n1", "INBOX") with { Precedence = "List", AutoSubmitted = "auto-generated" });
        gmail.Inner.AddMessage(NewMessage("n2", "INBOX"));
        gmail.Inner.AddMessage(NewMessage("n3"));
        gmail.Inner.SetLabels(Id(0), ["CATEGORY_UPDATES"]);
        gmail.Inner.DeleteMessage(Id(2));
        var job = await EnqueueAsync(IncrementalFetchJob.JobType);

        await RunNextAsync();

        var done = await GetJobAsync(job.Id);
        done.Status.ShouldBe("completed");
        done.Progress.ShouldBe(new JobProgress(5, 5, "Applying Gmail history"));
        gmail.MetadataCalls.SelectMany(c => c).ShouldBe(["n1", "n2", "n3", Id(0)], ignoreOrder: true);
        await using var db = postgres.CreateDbContext();
        (await db.Messages.CountAsync(Ct)).ShouldBe(MessageCount + 3);
        var n1 = await db.Messages.SingleAsync(m => m.Id == "n1", Ct);
        n1.LabelIds.ShouldBe(["INBOX"]);
        (n1.Precedence, n1.AutoSubmitted).ShouldBe(("list", "auto-generated"));
        (await db.Messages.SingleAsync(m => m.Id == "n2", Ct)).Precedence.ShouldBeNull();
        (await db.Messages.SingleAsync(m => m.Id == Id(0), Ct)).LabelIds.ShouldBe(["CATEGORY_UPDATES"]);
        (await db.Messages.SingleAsync(m => m.Id == Id(2), Ct)).DeletedInGmail.ShouldBeTrue();
        (await db.Messages.CountAsync(m => m.DeletedInGmail, Ct)).ShouldBe(1);
        (await db.FetchState.SingleAsync(Ct)).LastHistoryId.ShouldBe((await gmail.GetProfileAsync(Ct)).HistoryId);

        // m00002 was sender2's; deleted rows stay but no longer count.
        (await db.Senders.SingleAsync(s => s.Address == "new@example.com", Ct)).TotalCount.ShouldBe(3);
        (await db.Senders.SingleAsync(s => s.Address == "sender2@example.com", Ct)).TotalCount.ShouldBe((MessageCount / 4) - 1);
    }

    [Fact]
    public async Task BatchModify_label_changes_reach_the_local_store_through_history()
    {
        var label = await gmail.CreateLabelAsync("Synthetic/Applied", Ct);
        await gmail.BatchModifyAsync([Id(0), Id(1)], [label.Id], ["INBOX"], Ct);
        var job = await EnqueueAsync(IncrementalFetchJob.JobType);

        await RunNextAsync();

        (await GetJobAsync(job.Id)).Status.ShouldBe("completed");
        await using var db = postgres.CreateDbContext();
        (await db.Messages.SingleAsync(m => m.Id == Id(0), Ct)).LabelIds.ShouldBe(["CATEGORY_UPDATES", label.Id], ignoreOrder: true);
        (await db.Messages.SingleAsync(m => m.Id == Id(1), Ct)).LabelIds.ShouldBe(["CATEGORY_UPDATES", label.Id], ignoreOrder: true);
        (await db.Messages.SingleAsync(m => m.Id == Id(2), Ct)).LabelIds.ShouldContain("INBOX");
    }

    [Fact]
    public async Task Second_run_without_changes_touches_nothing()
    {
        gmail.Inner.SetLabels(Id(3), []);
        await EnqueueAsync(IncrementalFetchJob.JobType);
        await RunNextAsync();
        await using var db = postgres.CreateDbContext();
        var before = await db.Messages.MaxAsync(m => m.UpdatedAt, Ct);
        var historyId = (await db.FetchState.SingleAsync(Ct)).LastHistoryId;
        gmail.MetadataCalls.Clear();

        var job = await EnqueueAsync(IncrementalFetchJob.JobType);
        await RunNextAsync();

        (await GetJobAsync(job.Id)).Status.ShouldBe("completed");
        gmail.MetadataCalls.ShouldBeEmpty();
        (await db.Messages.MaxAsync(m => m.UpdatedAt, Ct)).ShouldBe(before);
        (await db.FetchState.AsNoTracking().SingleAsync(Ct)).LastHistoryId.ShouldBe(historyId);
        gmail.HistoryCalls.Last().StartHistoryId.ShouldBe(historyId);
    }

    [Fact]
    public async Task Expired_history_queues_a_full_resync_and_resets_the_mailbox_phase_keeping_the_Gmail_totals()
    {
        gmail.Inner.HistoryRetention = 2;
        for (var i = 0; i < 3; i++)
        {
            gmail.Inner.SetLabels(Id(i), ["STARRED"]);
        }

        await using (var seed = postgres.CreateDbContext())
        {
            await seed.FetchState.ExecuteUpdateAsync(
                f => f.SetProperty(r => r.InboxTotal, 3L).SetProperty(r => r.AllMailTotal, 5L), Ct);
        }

        var job = await EnqueueAsync(IncrementalFetchJob.JobType);

        await RunNextAsync();

        var done = await GetJobAsync(job.Id);
        done.Status.ShouldBe("completed");
        done.Progress.ShouldNotBeNull().Message.ShouldBe(IncrementalFetchJob.ExpiredMessage);
        await using var db = postgres.CreateDbContext();
        var state = await db.FetchState.SingleAsync(Ct);
        state.MailboxPhase.ShouldBe(MailboxPhase.NotStarted);
        state.PageToken.ShouldBeNull();
        // The last Gmail measurement stays as the status fallback.
        (state.InboxTotal, state.AllMailTotal).ShouldBe((3L, 5L));
        (await db.Jobs.SingleAsync(j => j.Type == MailboxFetchJob.JobType && j.Status == JobStatus.Queued, Ct)).Queue.ShouldBe(JobQueues.Fetch);
        gmail.MetadataCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Resync_after_expired_history_reconciles_the_stored_mail_it_does_not_list()
    {
        gmail.Inner.HistoryRetention = 2;
        gmail.Inner.DeleteMessage(Id(1));
        gmail.Inner.SetLabels(Id(3), ["TRASH"]);
        gmail.Inner.SetLabels(Id(5), ["STARRED"]);
        await EnqueueAsync(IncrementalFetchJob.JobType);
        await RunNextAsync();

        // A crash between the incremental commit and its enqueue: the reset phase makes the next Start a full fetch.
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Jobs.Where(j => j.Type == MailboxFetchJob.JobType && j.Status == JobStatus.Queued).ExecuteDeleteAsync(Ct)).ShouldBe(1);
        }

        await EnqueueAsync(MailboxFetchJob.JobType);
        gmail.MetadataCalls.Clear();
        await RunNextAsync();

        // Everything listed is stored already, so the fetch and the reconcile read labels only.
        gmail.MetadataCalls.ShouldBeEmpty();
        gmail.LabelsCalls.Last().ShouldBe([Id(1), Id(3)], ignoreOrder: true);
        await using var check = postgres.CreateDbContext();
        (await check.Messages.SingleAsync(m => m.Id == Id(1), Ct)).DeletedInGmail.ShouldBeTrue();
        var trashed = await check.Messages.SingleAsync(m => m.Id == Id(3), Ct);
        // In Trash: stored with its labels but not live, so it leaves sender3's count.
        trashed.DeletedInGmail.ShouldBeTrue();
        trashed.LabelIds.ShouldBe(["TRASH"]);
        (await check.Messages.SingleAsync(m => m.Id == Id(5), Ct)).LabelIds.ShouldBe(["STARRED"]);
        (await check.Senders.SingleAsync(x => x.Address == "sender1@example.com", Ct)).TotalCount.ShouldBe(MessageCount / 4 - 1);
        (await check.Senders.SingleAsync(x => x.Address == "sender3@example.com", Ct)).TotalCount.ShouldBe(MessageCount / 4 - 1);
        (await check.FetchState.SingleAsync(Ct)).MailboxPhase.ShouldBe(MailboxPhase.Completed);
        (await check.FetchRunMessages.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_page_token_rejected_on_every_replay_fails_the_job_after_the_cap()
    {
        gmail.Inner.HistoryPageSize = 1;
        for (var i = 0; i < 3; i++)
        {
            gmail.Inner.SetLabels(Id(i), ["STARRED"]);
        }

        string? historyId;
        await using (var db = postgres.CreateDbContext())
        {
            historyId = (await db.FetchState.SingleAsync(Ct)).LastHistoryId;
        }

        gmail.RejectHistoryPageToken = (_, token) => token is not null;
        var job = await EnqueueAsync(IncrementalFetchJob.JobType);

        await RunNextAsync();

        var failed = await GetJobAsync(job.Id);
        failed.Status.ShouldBe("failed");
        failed.Error.ShouldNotBeNull().ShouldContain("kept rejecting");
        gmail.HistoryCalls.Count.ShouldBe(6);
        await using var check = postgres.CreateDbContext();
        (await check.FetchState.SingleAsync(Ct)).LastHistoryId.ShouldBe(historyId);
    }

    [Fact]
    public async Task A_replay_after_a_rejected_page_token_counts_each_message_once()
    {
        gmail.Inner.HistoryPageSize = 2;
        for (var i = 0; i < 4; i++)
        {
            gmail.Inner.SetLabels(Id(i), ["STARRED"]);
        }

        gmail.RejectHistoryPageToken = (call, _) => call == 2;
        var job = await EnqueueAsync(IncrementalFetchJob.JobType);

        await RunNextAsync();

        var done = await GetJobAsync(job.Id);
        done.Status.ShouldBe("completed");
        gmail.HistoryCalls.Count.ShouldBe(4);
        var progress = done.Progress.ShouldNotBeNull();
        progress.Done.ShouldBe(4);
        progress.Total.ShouldBe(4);
    }

    [Fact]
    public async Task Pause_and_resume_mid_way_applies_no_page_twice()
    {
        gmail.Inner.HistoryPageSize = 2;
        for (var i = 0; i < 6; i++)
        {
            gmail.Inner.SetLabels(Id(i), ["STARRED"]);
        }

        var job = await EnqueueAsync(IncrementalFetchJob.JobType);
        gmail.AfterHistory = call => call == 1 ? WithAsync<IJobService, JobActionResult>(s => s.PauseAsync(job.Id, Ct)) : Task.CompletedTask;

        await RunNextAsync();

        (await GetJobAsync(job.Id)).Status.ShouldBe("paused");
        gmail.MetadataCalls.Count.ShouldBe(1);
        gmail.AfterHistory = null;
        (await WithAsync<IJobService, JobActionResult>(s => s.ResumeAsync(job.Id, Ct))).ShouldBe(JobActionResult.Ok);
        await RunNextAsync();

        (await GetJobAsync(job.Id)).Status.ShouldBe("completed");
        gmail.HistoryCalls.Count.ShouldBe(3);
        gmail.HistoryCalls.Select(c => c.PageToken).ShouldBeUnique();
        var fetched = gmail.MetadataCalls.SelectMany(c => c).ToList();
        fetched.ShouldBe(Enumerable.Range(0, 6).Select(Id), ignoreOrder: true);
        await using var db = postgres.CreateDbContext();
        (await db.Messages.CountAsync(m => m.LabelIds.Contains("STARRED"), Ct)).ShouldBe(6);
    }

    [Fact]
    public async Task New_spam_and_trash_mail_is_skipped()
    {
        gmail.Inner.AddMessage(NewMessage("s1", "SPAM"));
        gmail.Inner.AddMessage(NewMessage("t1", "TRASH"));
        gmail.Inner.AddMessage(NewMessage("n1", "INBOX"));

        await EnqueueAsync(IncrementalFetchJob.JobType);
        await RunNextAsync();

        await using var db = postgres.CreateDbContext();
        (await db.Messages.Where(m => m.Id == "s1" || m.Id == "t1" || m.Id == "n1").Select(m => m.Id).ToListAsync(Ct)).ShouldBe(["n1"]);
    }

    [Fact]
    public async Task A_touched_message_deleted_before_it_is_read_is_marked_deleted()
    {
        gmail.Inner.HistoryPageSize = 1;
        gmail.Inner.SetLabels(Id(4), ["STARRED"]);
        gmail.Inner.DeleteMessage(Id(4));

        await EnqueueAsync(IncrementalFetchJob.JobType);
        await RunNextAsync();

        await using var db = postgres.CreateDbContext();
        var row = await db.Messages.SingleAsync(m => m.Id == Id(4), Ct);
        row.DeletedInGmail.ShouldBeTrue();
        row.LabelIds.ShouldNotContain("STARRED");
    }

    /// <summary>Newest first, one minute apart; sender <c>i % 4</c>; even messages in the Inbox; plus one Spam message.</summary>
    private static List<FakeMessage> Seed() =>
    [
        .. Enumerable.Range(0, MessageCount).Select(i => new FakeMessage(
            Id(i), $"t{i:D5}", $"Sender {i % 4} <sender{i % 4}@example.com>", $"Synthetic subject {i}", Newest.AddMinutes(-i),
            i % 2 == 0 ? ["INBOX", "CATEGORY_UPDATES"] : ["CATEGORY_UPDATES"])),
        new("x00000", "x00000", "junk@example.com", "Synthetic junk", Newest, ["SPAM"]),
    ];

    private static string Id(int i) => string.Create(CultureInfo.InvariantCulture, $"m{i:D5}");

    private static FakeMessage NewMessage(string id, params string[] labels) =>
        new(id, id, "New Sender <new@example.com>", $"Synthetic new {id}", Newest.AddMinutes(1), labels);

    private Task<JobDto> EnqueueAsync(string type) =>
        WithAsync<IJobService, JobDto>(async s => (await s.EnqueueAsync(type, JobQueues.Fetch, null, Ct)).Job);

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
        // A completed fetch queues the sender stats rebuild on its own queue, so it may be claimed alongside.
        var claimed = await runner.ClaimAsync(Ct);
        claimed.ShouldNotBeEmpty();
        foreach (var id in claimed)
        {
            await runner.RunAsync(id, Ct);
        }
    }
}
