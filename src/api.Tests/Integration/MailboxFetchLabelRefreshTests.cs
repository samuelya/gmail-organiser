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

/// <summary>A mailbox fetch refreshes only the labels of mail an earlier fetch stored (#233).</summary>
[Collection(PostgresCollection.Name)]
public sealed class MailboxFetchLabelRefreshTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const int MessageCount = 10;
    private const string LocalSubject = "Synthetic local subject";
    private static readonly DateTimeOffset Newest = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // m0–m3 are in the Inbox; an earlier (sender) fetch stored these before the mailbox fetch runs.
    private static readonly string[] Prestored = ["m0", "m1", "m2", "m5", "m6", "m7"];

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
            await db.ActionLog.ExecuteDeleteAsync();
        }

        gmail = new CountingGmailClient(new FakeGmailClient(new FakeTokenStore(TimeProvider.System), Seed()));
        host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<IGmailClient>(gmail);
            services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner)));
        }));
        runner = ActivatorUtilities.CreateInstance<JobRunner>(host.Services);
    }

    /// <summary>Leaves fetch_state as migrated, for the later classes in the collection.</summary>
    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await postgres.ResetFetchStateAsync();
    }

    [Fact]
    public async Task Stored_mail_gets_a_labels_only_refresh_and_new_mail_full_metadata()
    {
        await WithAsync<MessageFetchPipeline, int>(p => p.UpsertByIdsAsync(Prestored, Ct));
        await using var db = postgres.CreateDbContext();
        await db.Messages.Where(m => m.Id == "m7").ExecuteUpdateAsync(s => s.SetProperty(m => m.Subject, LocalSubject), Ct);
        await db.Messages.Where(m => m.Id == "m2").ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, true), Ct);
        var before = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == "m0", Ct);
        gmail.Inner.SetLabels("m0", ["INBOX", "Label_1"]);
        gmail.Inner.SetLabels("m7", ["CATEGORY_SOCIAL", "UNREAD"]);
        gmail.Inner.SetLabels("m5", ["TRASH"]);
        gmail.Inner.DeleteMessage("m6");
        gmail.MetadataCalls.Clear();

        var job = await WithAsync<IJobService, JobDto>(async s => (await s.EnqueueAsync(MailboxFetchJob.JobType, MailboxFetchJob.Queue, null, Ct)).Job);
        await RunNextAsync();

        (await WithAsync<IJobService, JobDto?>(s => s.GetAsync(job.Id, Ct))).ShouldNotBeNull().Status.ShouldBe("completed");
        gmail.MetadataCalls.SelectMany(c => c).ShouldBe(["m3", "m4", "m8", "m9"], ignoreOrder: true);
        foreach (var id in Prestored.Except(["m6", "m5"]))
        {
            gmail.LabelsCalls.Count(c => c.Contains(id)).ShouldBe(1, id);
        }

        var rows = await db.Messages.AsNoTracking().ToDictionaryAsync(m => m.Id, Ct);
        rows.Count.ShouldBe(MessageCount);
        var read = rows["m0"];
        read.LabelIds.ShouldBe(["INBOX", "Label_1"]);
        read.Category.ShouldBeNull();
        read.Subject.ShouldBe(before.Subject);
        read.Snippet.ShouldBe(before.Snippet);
        read.FetchedAt.ShouldBe(before.FetchedAt);
        read.UpdatedAt.ShouldBeGreaterThan(before.UpdatedAt);
        rows["m7"].LabelIds.ShouldContain("UNREAD");
        rows["m7"].Category.ShouldBe(MessageCategory.Social);
        rows["m7"].Subject.ShouldBe(LocalSubject);
        rows["m2"].DeletedInGmail.ShouldBeFalse();
        rows["m5"].DeletedInGmail.ShouldBeTrue();
        rows["m5"].LabelIds.ShouldBe(["TRASH"]);
        rows["m6"].DeletedInGmail.ShouldBeTrue();

        // Label refreshes write local rows only.
        gmail.BatchModifyCalls.ShouldBeEmpty();
        (await db.ActionLog.CountAsync(Ct)).ShouldBe(0);

        // Every listed id counts: 4 Inbox ids, then 8 All Mail ids (m5 is in Trash, m6 is gone).
        var state = await db.FetchState.AsNoTracking().SingleAsync(Ct);
        state.InboxFetched.ShouldBe(4);
        state.AllMailFetched.ShouldBe(MessageCount - 2);
        state.AllMailTotal.ShouldBe(MessageCount - 2);
        (await db.Senders.SingleAsync(s => s.Address == "sender1@example.com", Ct)).TotalCount.ShouldBe(4);
    }

    [Fact]
    public async Task Refreshing_labels_never_inserts_rows()
    {
        await WithAsync<MessageFetchPipeline, int>(p => p.UpsertByIdsAsync(["m0"], Ct));

        var result = await WithAsync<MessageFetchPipeline, RefreshResult>(p => p.RefreshLabelsByIdsAsync(["m0", "m1", "missing"], Ct));

        result.ShouldBe(new RefreshResult(1, 0));
        await using var db = postgres.CreateDbContext();
        (await db.Messages.Select(m => m.Id).ToListAsync(Ct)).ShouldBe(["m0"]);
    }

    /// <summary>Newest first, one minute apart; sender <c>i % 2</c>; m0–m3 unread in the Inbox.</summary>
    private static List<FakeMessage> Seed() =>
    [
        .. Enumerable.Range(0, MessageCount).Select(i => new FakeMessage(
            $"m{i}", $"t{i}", $"Sender <sender{i % 2}@example.com>", $"Synthetic subject {i}", Newest.AddMinutes(-i),
            i < 4 ? ["INBOX", "UNREAD", "CATEGORY_UPDATES"] : ["CATEGORY_PROMOTIONS"])),
    ];

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
