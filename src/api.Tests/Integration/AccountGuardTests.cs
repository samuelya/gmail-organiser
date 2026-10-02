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

    private WebApplicationFactory<Program> host = null!;

    public async ValueTask InitializeAsync()
    {
        await ResetAsync();
        host = factory.WithWebHostBuilder(b => b
            .UseSetting("GMAIL_FAKE", "true")
            .ConfigureTestServices(services =>
            {
                services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner)));
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
        fetch.AccountEmail.ShouldBeNull();
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
        job.Error.ShouldBe($"{AccountGuard.ProblemTitle}. {AccountGuard.ProblemDetail}");
        job.Cursor.ShouldBeNull();
        (await db.FetchState.SingleAsync(Ct)).MailboxPhase.ShouldBe(MailboxPhase.NotStarted);
    }

    private async Task<AccountCheck> CheckAsync()
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAccountGuard>().CheckAsync(Ct);
    }

    private Task ConnectAsync(string account) =>
        host.Services.GetRequiredService<FakeTokenStore>().SaveAsync(account, FakeTokenStore.FakeRefreshToken, GmailScopes.All, Ct);

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
