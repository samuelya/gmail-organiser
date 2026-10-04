using System.Globalization;
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
public sealed class LabelResyncJobTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const int MessageCount = 40;
    private const int ChunkSize = 15;
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
        await WithAsync<ISettingsStore, AppSettings>(s => s.UpdateAsync(x => x with { FetchChunkSize = ChunkSize }, Ct));
    }

    /// <summary>Leaves fetch_state as migrated and no jobs or settings: later classes in the collection start clean.</summary>
    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.Jobs.ExecuteDeleteAsync();
            await db.Settings.ExecuteDeleteAsync();
        }

        await postgres.ResetFetchStateAsync();
    }

    [Fact]
    public async Task Run_refreshes_changed_labels_marks_deletions_revives_restored_rows_and_never_writes_Gmail()
    {
        await FetchMailboxAsync();
        string? historyId;
        await using (var db = postgres.CreateDbContext())
        {
            historyId = (await db.FetchState.SingleAsync(Ct)).LastHistoryId;
            await db.Messages.Where(m => m.Id == Id(5)).ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, true), Ct);
        }

        gmail.Inner.SetLabels(Id(0), ["STARRED"]);
        gmail.Inner.SetLabels(Id(1), ["INBOX", "UNREAD"]);
        gmail.Inner.DeleteMessage(Id(2));
        var undoBefore = await CountUndoAsync();
        var job = await EnqueueAsync();

        await RunNextAsync();

        var done = await GetJobAsync(job.Id);
        done.Status.ShouldBe("completed");
        done.Progress.ShouldBe(new JobProgress(MessageCount, MessageCount, LabelResyncJob.ProgressMessage));
        gmail.MetadataCalls.ShouldBeEmpty();
        gmail.BatchModifyCalls.ShouldBeEmpty();
        gmail.CreateLabelCalls.ShouldBeEmpty();
        gmail.LabelsCalls.SelectMany(c => c).ShouldBe(Enumerable.Range(0, MessageCount).Select(Id));
        await using var check = postgres.CreateDbContext();
        (await check.Messages.SingleAsync(m => m.Id == Id(0), Ct)).LabelIds.ShouldBe(["STARRED"]);
        (await check.Messages.SingleAsync(m => m.Id == Id(1), Ct)).LabelIds.ShouldBe(["INBOX", "UNREAD"], ignoreOrder: true);
        (await check.Messages.SingleAsync(m => m.Id == Id(2), Ct)).DeletedInGmail.ShouldBeTrue();
        (await check.Messages.SingleAsync(m => m.Id == Id(5), Ct)).DeletedInGmail.ShouldBeFalse();
        (await check.FetchState.SingleAsync(Ct)).LastHistoryId.ShouldBe(historyId);
        (await CountUndoAsync()).ShouldBe(undoBefore);
    }

    [Fact]
    public async Task A_run_paused_after_one_chunk_resumes_without_reading_that_chunk_again()
    {
        await FetchMailboxAsync();
        var job = await EnqueueAsync();
        gmail.AfterLabels = call => call == 1 ? WithAsync<IJobService, JobActionResult>(s => s.PauseAsync(job.Id, Ct)) : Task.CompletedTask;

        await RunNextAsync();

        var paused = await GetJobAsync(job.Id);
        paused.Status.ShouldBe("paused");
        paused.Progress.ShouldBe(new JobProgress(ChunkSize, MessageCount, LabelResyncJob.ProgressMessage));
        gmail.LabelsCalls.Count.ShouldBe(1);
        gmail.AfterLabels = null;
        gmail.Inner.SetLabels(Id(0), ["STARRED"]);
        (await WithAsync<IJobService, JobActionResult>(s => s.ResumeAsync(job.Id, Ct))).ShouldBe(JobActionResult.Ok);
        await RunNextAsync();

        (await GetJobAsync(job.Id)).Status.ShouldBe("completed");
        var read = gmail.LabelsCalls.SelectMany(c => c).ToList();
        read.ShouldBe(Enumerable.Range(0, MessageCount).Select(Id));
        await using var db = postgres.CreateDbContext();
        (await db.Messages.SingleAsync(m => m.Id == Id(0), Ct)).LabelIds.ShouldNotContain("STARRED");
    }

    [Fact]
    public async Task An_empty_store_completes_at_once_with_zero_of_zero_and_no_Gmail_reads()
    {
        var job = await EnqueueAsync();

        await RunNextAsync();

        var done = await GetJobAsync(job.Id);
        done.Status.ShouldBe("completed");
        done.Progress.ShouldBe(new JobProgress(0, 0, LabelResyncJob.ProgressMessage));
        gmail.LabelsCalls.ShouldBeEmpty();
        gmail.ProfileCalls.ShouldBe(0);
    }

    private async Task FetchMailboxAsync()
    {
        await WithAsync<IJobService, JobDto>(async s => (await s.EnqueueAsync(MailboxFetchJob.JobType, JobQueues.Fetch, null, Ct)).Job);
        await RunNextAsync();
        gmail.MetadataCalls.Clear();
        gmail.LabelsCalls.Clear();
    }

    private async Task<int> CountUndoAsync()
    {
        await using var db = postgres.CreateDbContext();
        return await db.ActionLog.CountAsync(Ct);
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

    private Task<JobDto> EnqueueAsync() =>
        WithAsync<IJobService, JobDto>(async s => (await s.EnqueueAsync(LabelResyncJob.JobType, LabelResyncJob.Queue, null, Ct)).Job);

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
}
