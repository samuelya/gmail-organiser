using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Llm;
using GmailOrganiser.Setup;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class AccountGuardTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string OtherAccount = "other@example.com";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CountingJobState counting = new();
    private WebApplicationFactory<Program> host = null!;

    public async ValueTask InitializeAsync()
    {
        await ResetAsync();
        host = factory.WithWebHostBuilder(b => b
            .UseSetting("GMAIL_FAKE", "true")
            .ConfigureTestServices(services =>
            {
                services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner)));
                services.AddSingleton(counting);
                services.AddKeyedScoped<IJobHandler, CountingJobHandler>(CountingJobHandler.JobType);
                services.AddHttpClient(OllamaHttp.ClientName).ConfigurePrimaryHttpMessageHandler(
                    () => new StubOllamaHandler { Failure = new HttpRequestException("synthetic refusal") });
            }));
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await ResetAsync();
    }

    [Fact]
    public async Task Start_is_allowed_without_local_data()
    {
        (await CheckAsync()).Status.ShouldBe(AccountCheckStatus.NoLocalData);

        (await PostAsync("/api/fetch/mailbox/start")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Start_is_allowed_for_the_same_account_in_any_case()
    {
        await SetLocalAccountAsync("  " + FakeGmailClient.AccountEmail.ToUpperInvariant());

        (await CheckAsync()).Status.ShouldBe(AccountCheckStatus.Ok);
        (await PostAsync("/api/fetch/mailbox/start")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task A_different_account_blocks_start_and_shows_in_both_statuses_until_the_original_reconnects()
    {
        await SetLocalAccountAsync(FakeGmailClient.AccountEmail);
        await ConnectAsync(OtherAccount);

        var response = await PostAsync("/api/fetch/mailbox/start");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("type").GetString().ShouldBe(AccountGuard.ProblemType);
        problem.GetProperty("title").GetString().ShouldBe("Local data belongs to a different Gmail account");
        problem.GetProperty("localAccount").GetString().ShouldBe("u***@example.com");
        var body = problem.GetRawText();
        body.ShouldNotContain(FakeGmailClient.AccountEmail);
        body.ShouldNotContain(OtherAccount);
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Jobs.CountAsync(Ct)).ShouldBe(0);
        }

        var fetch = (await host.CreateClient().GetFromJsonAsync<FetchStatusDto>("/api/fetch/status", Ct)).ShouldNotBeNull();
        fetch.AccountMismatch.ShouldBeTrue();
        fetch.LocalAccount.ShouldBe("u***@example.com");
        fetch.AccountEmail.ShouldBe(FakeGmailClient.AccountEmail);
        var setup = (await host.CreateClient().GetFromJsonAsync<SetupStatusDto>("/api/setup/status", Ct)).ShouldNotBeNull();
        setup.AccountMismatch.ShouldBeTrue();

        await ConnectAsync(FakeGmailClient.AccountEmail);

        (await PostAsync("/api/fetch/mailbox/start")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        fetch = (await host.CreateClient().GetFromJsonAsync<FetchStatusDto>("/api/fetch/status", Ct)).ShouldNotBeNull();
        fetch.AccountMismatch.ShouldBeFalse();
        fetch.LocalAccount.ShouldBeNull();
        fetch.AccountEmail.ShouldBe(FakeGmailClient.AccountEmail);
        (await host.CreateClient().GetFromJsonAsync<SetupStatusDto>("/api/setup/status", Ct)).ShouldNotBeNull().AccountMismatch.ShouldBeFalse();
    }

    [Fact]
    public async Task A_different_account_blocks_a_sender_fetch_start()
    {
        await SetLocalAccountAsync(FakeGmailClient.AccountEmail);
        await ConnectAsync(OtherAccount);

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        var response = await client.PostAsJsonAsync("/api/fetch/sender", new SenderFetchRequest("sender@example.com"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe(AccountGuard.ProblemType);
        await using var db = postgres.CreateDbContext();
        (await db.Jobs.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_different_account_blocks_an_incremental_fetch_start()
    {
        await using (var setup = postgres.CreateDbContext())
        {
            await setup.FetchState.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.AccountEmail, FakeGmailClient.AccountEmail)
                .SetProperty(r => r.MailboxPhase, MailboxPhase.Completed)
                .SetProperty(r => r.LastHistoryId, "1000"), Ct);
        }

        await ConnectAsync(OtherAccount);

        var response = await PostAsync("/api/fetch/incremental");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe(AccountGuard.ProblemType);
        await using var db = postgres.CreateDbContext();
        (await db.Jobs.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_fetch_job_queued_before_a_reconnect_to_another_account_fails_instead_of_running()
    {
        await SetLocalAccountAsync(FakeGmailClient.AccountEmail);
        var started = (await (await PostAsync("/api/fetch/mailbox/start")).Content.ReadFromJsonAsync<StartFetchResponse>(Ct)).ShouldNotBeNull();
        await ConnectAsync(OtherAccount);
        var runner = ActivatorUtilities.CreateInstance<JobRunner>(host.Services);

        (await runner.ClaimAsync(Ct)).ShouldBe([started.JobId]);
        await runner.RunAsync(started.JobId, Ct);

        await using var db = postgres.CreateDbContext();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == started.JobId, Ct);
        job.Status.ShouldBe(JobStatus.Failed);
        job.Error.ShouldBe(AccountGuard.RefuseReason);
        job.Cursor.ShouldBeNull();
        (await db.FetchState.SingleAsync(Ct)).MailboxPhase.ShouldBe(MailboxPhase.NotStarted);
    }

    [Fact]
    public async Task A_reconnect_to_another_account_mid_run_fails_the_guarded_job_at_its_next_checkpoint()
    {
        await SetLocalAccountAsync(FakeGmailClient.AccountEmail);
        await using var guarded = host.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddKeyedScoped<IJobRunGuard, FetchAccountJobGuard>(CountingJobHandler.JobType)));
        counting.AfterStep = (_, step) => step == 2 ? ConnectAsync(guarded, OtherAccount) : Task.CompletedTask;
        var job = await EnqueueCountingAsync(guarded);
        var runner = ActivatorUtilities.CreateInstance<JobRunner>(guarded.Services);

        await runner.ClaimAsync(Ct);
        await runner.RunAsync(job.Id, Ct);

        await using var db = postgres.CreateDbContext();
        var row = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct);
        row.Status.ShouldBe(JobStatus.Failed);
        row.Error.ShouldBe(AccountGuard.RefuseReason);
        counting.Executed.ShouldBe([1, 2]);
        JsonSerializer.Deserialize<CountingCursor>(row.Cursor.ShouldNotBeNull(), JsonSerializerOptions.Web)
            .ShouldBe(new CountingCursor(2), "the cursor stays at the last allowed checkpoint");
    }

    [Fact]
    public async Task A_job_on_the_fetch_queue_that_does_not_read_Gmail_runs_while_the_accounts_differ()
    {
        await SetLocalAccountAsync(FakeGmailClient.AccountEmail);
        await ConnectAsync(host, OtherAccount);
        var job = await EnqueueCountingAsync(host);
        var runner = ActivatorUtilities.CreateInstance<JobRunner>(host.Services);

        await runner.ClaimAsync(Ct);
        await runner.RunAsync(job.Id, Ct);

        await using var db = postgres.CreateDbContext();
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct)).Status.ShouldBe(JobStatus.Completed);
        counting.Executed.ShouldBe([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task Every_Gmail_reading_job_type_has_the_account_guard()
    {
        await using var scope = host.Services.CreateAsyncScope();
        foreach (var type in FetchJobTypes.ReadsGmail)
        {
            scope.ServiceProvider.GetKeyedService<IJobRunGuard>(type).ShouldBeOfType<FetchAccountJobGuard>();
        }

        scope.ServiceProvider.GetKeyedService<IJobRunGuard>(JobQueues.Fetch).ShouldBeNull();
    }

    [Fact]
    public async Task The_claim_stamps_the_first_account_and_refuses_another()
    {
        await using var scope = host.Services.CreateAsyncScope();
        var claim = scope.ServiceProvider.GetRequiredService<LocalAccountClaim>();

        await claim.ClaimAsync(FakeGmailClient.AccountEmail, Ct);
        await claim.ClaimAsync(FakeGmailClient.AccountEmail.ToUpperInvariant(), Ct);
        var refused = await Should.ThrowAsync<JobRefusedException>(() => claim.ClaimAsync(OtherAccount, Ct));

        refused.Message.ShouldBe(AccountGuard.RefuseReason);
        await using var db = postgres.CreateDbContext();
        (await db.FetchState.SingleAsync(Ct)).AccountEmail.ShouldBe(FakeGmailClient.AccountEmail);
    }

    [Fact]
    public async Task Connecting_another_account_is_refused_only_while_a_Gmail_reading_job_is_active()
    {
        (await RefusesConnectAsync(OtherAccount)).ShouldBeFalse("no job is active");

        (await PostAsync("/api/fetch/mailbox/start")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        (await RefusesConnectAsync(OtherAccount)).ShouldBeTrue("a queued mailbox fetch for the connected account");
        (await RefusesConnectAsync(" " + FakeGmailClient.AccountEmail.ToUpperInvariant())).ShouldBeFalse("the same account");
        await using var db = postgres.CreateDbContext();
        await db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Completed), Ct);
        (await RefusesConnectAsync(OtherAccount)).ShouldBeFalse("the fetch is no longer active");
    }

    private async Task<bool> RefusesConnectAsync(string account)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAccountGuard>().RefusesConnectAsync(account, Ct);
    }

    private static async Task<JobDto> EnqueueCountingAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IJobService>().EnqueueAsync(CountingJobHandler.JobType, JobQueues.Fetch, ct: Ct)).Job;
    }

    private async Task<AccountCheck> CheckAsync()
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAccountGuard>().CheckAsync(Ct);
    }

    private Task ConnectAsync(string account) => ConnectAsync(host, account);

    private static Task ConnectAsync(WebApplicationFactory<Program> app, string account) =>
        app.Services.GetRequiredService<FakeTokenStore>().SaveAsync(account, FakeTokenStore.FakeRefreshToken, GmailScopes.All, Ct);

    private async Task SetLocalAccountAsync(string account)
    {
        await using var db = postgres.CreateDbContext();
        await db.FetchState.ExecuteUpdateAsync(s => s.SetProperty(r => r.AccountEmail, account), Ct);
    }

    private Task<HttpResponseMessage> PostAsync(string path)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client.PostAsync(path, null, Ct);
    }

    private async Task ResetAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Jobs.ExecuteDeleteAsync();
        await db.FetchState.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.AccountEmail, (string?)null)
            .SetProperty(r => r.MailboxPhase, MailboxPhase.NotStarted)
            .SetProperty(r => r.PageToken, (string?)null));
    }
}
