using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class FetchEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Jobs.ExecuteDeleteAsync();
        await db.FetchRunMessages.ExecuteDeleteAsync();
        await db.Messages.ExecuteDeleteAsync();
        await db.Senders.ExecuteDeleteAsync();
        await db.OAuthTokens.ExecuteDeleteAsync();
        await db.FetchState.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.AccountEmail, (string?)null)
            .SetProperty(r => r.MailboxPhase, MailboxPhase.NotStarted)
            .SetProperty(r => r.PageToken, (string?)null)
            .SetProperty(r => r.InboxFetched, 0)
            .SetProperty(r => r.AllMailFetched, 0)
            .SetProperty(r => r.MessagesTotal, (long?)null)
            .SetProperty(r => r.InboxTotal, (long?)null)
            .SetProperty(r => r.AllMailTotal, (long?)null)
            .SetProperty(r => r.LastHistoryId, (string?)null)
            .SetProperty(r => r.StartedAt, (DateTimeOffset?)null)
            .SetProperty(r => r.CompletedAt, (DateTimeOffset?)null));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Status_before_any_fetch_is_not_started_with_zeros()
    {
        await using var host = FakeGmailHost();

        var status = await host.CreateClient().GetFromJsonAsync<FetchStatusDto>("/api/fetch/status", Ct);

        status.ShouldBe(new FetchStatusDto(null, "not_started", 0, 0, null, 0, 0, null, null, null, null, null, false, null, null, null));
    }

    [Fact]
    public async Task Start_without_a_Gmail_connection_is_a_409_problem()
    {
        await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(RemoveRunner));

        var response = await PostStartAsync(host);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        problem.ShouldNotBeNull().Title.ShouldBe("Gmail not connected");
        await using var db = postgres.CreateDbContext();
        (await db.Jobs.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Start_twice_returns_202_then_200_with_the_same_job()
    {
        await using var host = FakeGmailHost();

        var first = await PostStartAsync(host);
        var second = await PostStartAsync(host);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        first.Headers.Location?.OriginalString.ShouldStartWith("/api/jobs/");
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var started = (await first.Content.ReadFromJsonAsync<StartFetchResponse>(Ct)).ShouldNotBeNull();
        (await second.Content.ReadFromJsonAsync<StartFetchResponse>(Ct)).ShouldBe(started);

        var status = await host.CreateClient().GetFromJsonAsync<FetchStatusDto>("/api/fetch/status", Ct);
        var active = status.ShouldNotBeNull().ActiveJob.ShouldNotBeNull();
        active.Id.ShouldBe(started.JobId);
        active.Type.ShouldBe(MailboxFetchJob.JobType);
        active.Status.ShouldBe("queued");
    }

    [Fact]
    public async Task Status_after_a_fake_fetch_run_has_the_phase_and_counts_and_no_active_job()
    {
        await using var host = FakeGmailHost();
        var started = await (await PostStartAsync(host)).Content.ReadFromJsonAsync<StartFetchResponse>(Ct);
        var runner = ActivatorUtilities.CreateInstance<JobRunner>(host.Services);
        var id = (await runner.ClaimAsync(Ct)).ShouldHaveSingleItem();
        id.ShouldBe(started.ShouldNotBeNull().JobId);
        await runner.RunAsync(id, Ct);

        await using (var db = postgres.CreateDbContext())
        {
            // Stored rows deleted in Gmail are not counted.
            await db.Messages.Where(m => m.Id == db.Messages.OrderBy(x => x.Id).Select(x => x.Id).First())
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, true), Ct);
        }

        var status = (await host.CreateClient().GetFromJsonAsync<FetchStatusDto>("/api/fetch/status", Ct)).ShouldNotBeNull();

        status.MailboxPhase.ShouldBe("completed");
        status.AccountEmail.ShouldBe(FakeGmailClient.AccountEmail);
        status.MessagesStored.ShouldBe(FakeMailboxSeed.MessageCount - 1);
        status.SendersCount.ShouldBeGreaterThan(0);
        status.AllMailFetched.ShouldBeGreaterThan(0);
        status.InboxFetched.ShouldBeGreaterThan(0);
        status.InboxTotal.ShouldBe(status.InboxFetched);
        status.AllMailTotal.ShouldBe(status.AllMailFetched);
        status.LastHistoryId.ShouldNotBeNull();
        status.StartedAt.ShouldNotBeNull();
        status.CompletedAt.ShouldNotBeNull();
        status.ActiveJob.ShouldBeNull();
        await using var check = postgres.CreateDbContext();
        status.SendersCount.ShouldBe(await check.Senders.LongCountAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_starts_answer_one_202_and_200s_with_the_same_job()
    {
        await using var host = FakeGmailHost();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => PostStartAsync(host), Ct)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(5);
        var ids = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<StartFetchResponse>(Ct)));
        ids.Select(r => r.ShouldNotBeNull().JobId).Distinct().ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Paused)]
    public async Task Start_after_a_failed_or_paused_fetch_resumes_that_job_with_its_cursor(JobStatus status)
    {
        await using var host = FakeGmailHost();
        var id = await AddMailboxJobAsync(status, "{\"pageToken\":\"p-7\"}", status == JobStatus.Failed ? "Gmail unavailable" : null);

        var response = await PostStartAsync(host);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<StartFetchResponse>(Ct)).ShouldBe(new StartFetchResponse(id));
        await using var db = postgres.CreateDbContext();
        var job = (await db.Jobs.ToListAsync(Ct)).ShouldHaveSingleItem();
        job.Status.ShouldBe(JobStatus.Queued);
        JsonDocument.Parse(job.Cursor.ShouldNotBeNull()).RootElement.GetProperty("pageToken").GetString().ShouldBe("p-7");
        job.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Start_after_a_completed_fetch_queues_the_incremental_fetch()
    {
        await using var host = FakeGmailHost();
        await AddMailboxJobAsync(JobStatus.Completed, null, null);
        await CompleteMailboxFetchAsync("1000");

        var response = await PostStartAsync(host);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var started = (await response.Content.ReadFromJsonAsync<StartFetchResponse>(Ct)).ShouldNotBeNull();
        await using var check = postgres.CreateDbContext();
        (await check.Jobs.SingleAsync(j => j.Id == started.JobId, Ct)).Type.ShouldBe(FetchJobTypes.Incremental);
        (await check.Jobs.CountAsync(j => j.Type == MailboxFetchJob.JobType, Ct)).ShouldBe(1);
        var status = (await host.CreateClient().GetFromJsonAsync<FetchStatusDto>("/api/fetch/status", Ct)).ShouldNotBeNull();
        var active = status.ActiveJob.ShouldNotBeNull();
        active.Id.ShouldBe(started.JobId);
        active.Type.ShouldBe(FetchJobTypes.Incremental);
    }

    [Fact]
    public async Task Status_reports_a_failed_incremental_fetch_after_the_completed_mailbox_fetch()
    {
        await using var host = FakeGmailHost();
        await AddMailboxJobAsync(JobStatus.Completed, null, null);
        await CompleteMailboxFetchAsync("1000");
        var failed = NewJob(FetchJobTypes.Incremental, JobStatus.Failed, null, "Gmail unavailable");
        failed.CreatedAt = failed.CreatedAt.AddSeconds(1);
        await using (var db = postgres.CreateDbContext())
        {
            db.Jobs.Add(failed);
            await db.SaveChangesAsync(Ct);
        }

        var status = (await host.CreateClient().GetFromJsonAsync<FetchStatusDto>("/api/fetch/status", Ct)).ShouldNotBeNull();

        status.ActiveJob.ShouldBeNull();
        var job = status.FailedJob.ShouldNotBeNull();
        job.Id.ShouldBe(failed.Id);
        job.Type.ShouldBe(FetchJobTypes.Incremental);
    }

    [Theory]
    [InlineData(MailboxPhase.AllMail, "1000")]
    [InlineData(MailboxPhase.Completed, null)]
    public async Task Incremental_before_a_completed_mailbox_fetch_is_a_409_problem(MailboxPhase phase, string? lastHistoryId)
    {
        await using var host = FakeGmailHost();
        await using (var db = postgres.CreateDbContext())
        {
            await db.FetchState.ExecuteUpdateAsync(s => s.SetProperty(r => r.MailboxPhase, phase).SetProperty(r => r.LastHistoryId, lastHistoryId), Ct);
        }

        var response = await PostStartAsync(host, "/api/fetch/incremental");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct)).ShouldNotBeNull().Detail.ShouldNotBeNull().ShouldContain("run the mailbox fetch first");
        await using var check = postgres.CreateDbContext();
        (await check.Jobs.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Incremental_twice_returns_202_then_200_with_the_same_job()
    {
        await using var host = FakeGmailHost();
        await CompleteMailboxFetchAsync("1000");

        var first = await PostStartAsync(host, "/api/fetch/incremental");
        var second = await PostStartAsync(host, "/api/fetch/incremental");

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var id = (await first.Content.ReadFromJsonAsync<StartFetchResponse>(Ct)).ShouldNotBeNull().JobId;
        (await second.Content.ReadFromJsonAsync<StartFetchResponse>(Ct)).ShouldNotBeNull().JobId.ShouldBe(id);
    }

    [Fact]
    public async Task Status_reports_the_failed_mailbox_job_and_ignores_other_fetch_queue_jobs()
    {
        await using var host = FakeGmailHost();
        var failed = await AddMailboxJobAsync(JobStatus.Failed, null, "Gmail unavailable");
        await using (var db = postgres.CreateDbContext())
        {
            db.Jobs.Add(NewJob(FetchJobTypes.Sender, JobStatus.Running, "{\"target\":\"example.com\"}", null));
            await db.SaveChangesAsync(Ct);
        }

        var status = (await host.CreateClient().GetFromJsonAsync<FetchStatusDto>("/api/fetch/status", Ct)).ShouldNotBeNull();

        status.ActiveJob.ShouldBeNull();
        var job = status.FailedJob.ShouldNotBeNull();
        job.Id.ShouldBe(failed);
        job.Status.ShouldBe("failed");
        job.Error.ShouldBe("Gmail unavailable");
    }

    private async Task<Guid> AddMailboxJobAsync(JobStatus status, string? cursor, string? error)
    {
        await using var db = postgres.CreateDbContext();
        var row = NewJob(MailboxFetchJob.JobType, status, cursor, error);
        db.Jobs.Add(row);
        await db.SaveChangesAsync(Ct);
        return row.Id;
    }

    private static JobRow NewJob(string type, JobStatus status, string? cursor, string? error)
    {
        var now = DateTimeOffset.UtcNow;
        return new JobRow
        {
            Id = Guid.NewGuid(),
            Type = type,
            Queue = JobQueues.Fetch,
            Status = status,
            Cursor = cursor,
            Error = error,
            CreatedAt = now,
            UpdatedAt = now,
            FinishedAt = JobRow.Finished.Contains(status) ? now : null,
        };
    }

    private WebApplicationFactory<Program> FakeGmailHost() =>
        factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true").ConfigureTestServices(RemoveRunner));

    private static void RemoveRunner(IServiceCollection services) =>
        services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner)));

    private async Task CompleteMailboxFetchAsync(string lastHistoryId)
    {
        await using var db = postgres.CreateDbContext();
        await db.FetchState.ExecuteUpdateAsync(
            s => s.SetProperty(r => r.MailboxPhase, MailboxPhase.Completed).SetProperty(r => r.LastHistoryId, lastHistoryId), Ct);
    }

    private static Task<HttpResponseMessage> PostStartAsync(WebApplicationFactory<Program> host, string path = "/api/fetch/mailbox/start")
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client.PostAsync(path, null, Ct);
    }
}
