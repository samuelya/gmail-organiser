using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
using GmailOrganiser.CleanUp;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class DataPurgeTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private const string SettingsDocument = """{"chatModel": "chat-model-a"}""";

    private WebApplicationFactory<Program> host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // Every model table, so tables added later are emptied too; the fetch_state singleton is reset below.
        await using (var db = postgres.CreateDbContext())
        {
            var sql = db.GetService<ISqlGenerationHelper>();
            var tables = db.Model.GetEntityTypes().Where(t => t.ClrType != typeof(FetchStateRow)).Select(t => sql.DelimitIdentifier(t.GetTableName()!, t.GetSchema())).Distinct();
            var truncate = $"TRUNCATE {string.Join(", ", tables)} CASCADE";
            await db.Database.ExecuteSqlRawAsync(truncate, Ct);
        }

        await postgres.ResetFetchStateAsync();

        // No live job runner: it would claim the synthetic jobs and fail them before the purge checks.
        host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner)))));
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync();
        await db.OAuthTokens.ExecuteDeleteAsync();
        await postgres.ResetFetchStateAsync();
    }

    [Fact]
    public async Task Purge_empties_mail_tables_and_keeps_settings_and_connection()
    {
        await SeedAsync();

        var result = await PurgeAsync();

        var done = result.ShouldBeOfType<PurgeResult.Done>();
        done.Tables.ShouldContain("messages");
        done.Tables.ShouldContain("fetch_state");
        done.Tables.ShouldNotContain("settings");
        done.Tables.ShouldNotContain("oauth_tokens");
        await using var db = postgres.CreateDbContext();
        (await db.Messages.AnyAsync(Ct)).ShouldBeFalse();
        (await db.Senders.AnyAsync(Ct)).ShouldBeFalse();
        (await db.Jobs.AnyAsync(Ct)).ShouldBeFalse();
        (await db.AnalysisRuns.AnyAsync(Ct)).ShouldBeFalse();
        (await db.Suggestions.AnyAsync(Ct)).ShouldBeFalse();
        (await db.Decisions.AnyAsync(Ct)).ShouldBeFalse();
        (await db.ActionBatches.AnyAsync(Ct)).ShouldBeFalse();
        (await db.ActionLog.AnyAsync(Ct)).ShouldBeFalse();
        (await db.ExternalReviews.AnyAsync(Ct)).ShouldBeFalse();
        JsonNode.Parse((await db.Settings.AsNoTracking().SingleAsync(Ct)).Document)!["chatModel"]!.GetValue<string>().ShouldBe("chat-model-a");
        var token = await db.OAuthTokens.AsNoTracking().SingleAsync(Ct);
        token.AccountEmail.ShouldBe("owner@example.com");
        token.RefreshTokenProtected.ShouldBe("protected-synthetic");
        var state = await db.FetchState.AsNoTracking().SingleAsync(Ct);
        state.Id.ShouldBe(FetchStateRow.SingletonId);
        state.MailboxPhase.ShouldBe(MailboxPhase.NotStarted);
        state.AccountEmail.ShouldBeNull();
        state.LastHistoryId.ShouldBeNull();
        state.PageToken.ShouldBeNull();
        state.InboxFetched.ShouldBe(0);
        state.UpdatedAt.ShouldBe(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task Table_list_is_every_model_table_but_settings_and_oauth_tokens()
    {
        await using var db = postgres.CreateDbContext();
        var expected = db.Model.GetEntityTypes().Select(t => t.GetTableName()!)
            .Where(t => t is not "settings" and not "oauth_tokens").Distinct().Order(StringComparer.Ordinal);

        (await PurgeAsync()).ShouldBeOfType<PurgeResult.Done>().Tables.ShouldBe(expected);
    }

    [Fact]
    public async Task Second_purge_on_an_empty_database_succeeds()
    {
        (await PurgeAsync()).ShouldBeOfType<PurgeResult.Done>();
        (await PurgeAsync()).ShouldBeOfType<PurgeResult.Done>();

        await using var db = postgres.CreateDbContext();
        (await db.FetchState.CountAsync(Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Paused)]
    public async Task Active_job_refuses_the_purge_and_keeps_every_row(JobStatus status)
    {
        await SeedAsync();
        await using (var db = postgres.CreateDbContext())
        {
            db.Jobs.Add(Job(status));
            await db.SaveChangesAsync(Ct);
        }

        (await PurgeAsync()).ShouldBeOfType<PurgeResult.JobsActive>();

        await using var check = postgres.CreateDbContext();
        (await check.Messages.CountAsync(Ct)).ShouldBe(1);
        (await check.Jobs.CountAsync(Ct)).ShouldBe(2);
        (await check.FetchState.AsNoTracking().SingleAsync(Ct)).MailboxPhase.ShouldBe(MailboxPhase.Completed);
    }

    [Theory]
    [InlineData(ReviewJobTypes.Apply)]
    [InlineData(ReviewJobTypes.Undo)]
    [InlineData(CleanUpJobTypes.Actions)]
    public async Task Failed_gmail_job_with_a_pending_chunk_refuses_the_purge_and_keeps_the_undo_log(string type)
    {
        await SeedAsync();
        await using (var db = postgres.CreateDbContext())
        {
            db.Jobs.Add(Job(JobStatus.Failed, type, """{"pending": {"index": 0}}"""));
            db.Jobs.Add(Job(JobStatus.Failed, type, """{"next": 1}"""));
            await db.SaveChangesAsync(Ct);
        }

        (await PurgeAsync()).ShouldBeOfType<PurgeResult.GmailChunkPending>();

        await using var check = postgres.CreateDbContext();
        (await check.ActionLog.CountAsync(Ct)).ShouldBe(1);
        (await check.Jobs.CountAsync(Ct)).ShouldBe(3);
    }

    [Fact]
    public async Task Purge_waits_for_a_batch_start_holding_action_batches_and_then_sees_its_job()
    {
        // ApplyService.StartAsync order: the batch row, then the job, in one transaction.
        await using var apply = postgres.CreateDbContext();
        await using var tx = await apply.Database.BeginTransactionAsync(Ct);
        apply.ActionBatches.Add(new ActionBatchRow { Id = Guid.NewGuid(), Kind = ActionKind.ApplyRest, Description = "Synthetic batch", MessageCount = 1, CreatedAt = Now });
        await apply.SaveChangesAsync(Ct);

        var purge = PurgeAsync();
        await using (var probe = postgres.CreateDbContext())
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!await probe.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock'").AnyAsync(Ct))
            {
                DateTime.UtcNow.ShouldBeLessThan(deadline);
                await Task.Delay(20, Ct);
            }
        }

        apply.Jobs.Add(Job(JobStatus.Queued, ReviewJobTypes.Apply));
        await apply.SaveChangesAsync(Ct);
        await tx.CommitAsync(Ct);

        (await purge.WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBeOfType<PurgeResult.JobsActive>();
    }

    [Fact]
    public async Task Failed_jobs_without_a_pending_gmail_chunk_do_not_block_the_purge()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Jobs.Add(Job(JobStatus.Failed, ReviewJobTypes.Apply, """{"next": 1}"""));
            db.Jobs.Add(Job(JobStatus.Failed, "synthetic-job", """{"pending": {"index": 0}}"""));
            await db.SaveChangesAsync(Ct);
        }

        (await PurgeAsync()).ShouldBeOfType<PurgeResult.Done>();
    }

    [Fact]
    public async Task Endpoint_purges_with_the_confirmation_word()
    {
        await SeedAsync();

        var response = await PostAsync(new { confirm = " purge " });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<PurgeResponse>(Ct)).ShouldNotBeNull();
        body.Tables.ShouldContain("messages");
        await using var db = postgres.CreateDbContext();
        (await db.Messages.AnyAsync(Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Endpoint_purge_tells_connected_clients()
    {
        var purged = new TaskCompletionSource();
        var server = host.Server;
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, JobsHub.Path), HttpTransportType.LongPolling, o =>
            {
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.Headers["Origin"] = ApiFactory.AllowedOrigin;
            })
            .Build();
        connection.On(JobsHub.DataPurgedEvent, () => purged.TrySetResult());
        await connection.StartAsync(Ct);

        (await PostAsync(new { confirm = "purge" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        await purged.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public async Task Endpoint_with_no_body_is_a_field_error(string body)
    {
        var response = await Client().PostAsync(
            "/api/settings/purge", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        problem["errors"]!["confirm"].ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Purge")]
    [InlineData("PURGE")]
    [InlineData("delete")]
    public async Task Endpoint_without_the_exact_word_is_a_field_error(string? confirm)
    {
        await SeedAsync();

        var response = await PostAsync(new { confirm });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        problem["errors"]!["confirm"]!.AsArray().Single()!.GetValue<string>().ShouldBe("Type purge to confirm.");
        await using var db = postgres.CreateDbContext();
        (await db.Messages.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Endpoint_with_an_active_job_is_409()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Jobs.Add(Job(JobStatus.Queued));
            await db.SaveChangesAsync(Ct);
        }

        var response = await PostAsync(new { confirm = "purge" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        problem["title"]!.GetValue<string>().ShouldBe("Jobs are active");
    }

    [Fact]
    public async Task Endpoint_without_the_requested_with_header_is_forbidden()
    {
        await SeedAsync();

        var response = await host.CreateClient().PostAsJsonAsync("/api/settings/purge", new { confirm = "purge" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await using var db = postgres.CreateDbContext();
        (await db.Messages.CountAsync(Ct)).ShouldBe(1);
    }

    private async Task<PurgeResult> PurgeAsync()
    {
        await using var db = postgres.CreateDbContext();
        using var labels = new LabelCatalog(host.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        var hub = host.Services.GetRequiredService<IHubContext<JobsHub>>();
        return await new DataPurgeService(db, labels, hub, NullLogger<DataPurgeService>.Instance).PurgeAsync(Ct);
    }

    private Task<HttpResponseMessage> PostAsync(object body) => Client().PostAsJsonAsync("/api/settings/purge", body, Ct);

    private HttpClient Client()
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ApiRequestGuardMiddleware.RequestedWithHeader, "XMLHttpRequest");
        return client;
    }

    private static JobRow Job(JobStatus status, string type = "synthetic-job", string? cursor = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Queue = "synthetic-queue",
        Status = status,
        Cursor = cursor,
        CreatedAt = Now,
        QueuedAt = Now,
        UpdatedAt = Now,
    };

    private async Task SeedAsync()
    {
        var runId = Guid.NewGuid();
        var suggestionId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        await using var db = postgres.CreateDbContext();
        await db.Database.ExecuteSqlAsync($"INSERT INTO settings (id, document) VALUES ({SettingsRow.SingletonId}, {SettingsDocument}::jsonb)", Ct);
        db.OAuthTokens.Add(new OAuthTokenRow
        {
            AccountEmail = "owner@example.com",
            RefreshTokenProtected = "protected-synthetic",
            Scopes = ["scope-a"],
            ConnectedAt = Now,
            UpdatedAt = Now,
        });
        await db.FetchState.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.AccountEmail, "owner@example.com")
            .SetProperty(r => r.MailboxPhase, MailboxPhase.Completed)
            .SetProperty(r => r.LastHistoryId, "12345"), Ct);
        db.Messages.Add(new MessageRow
        {
            Id = "msg-1",
            ThreadId = "thread-1",
            FromAddress = "news@example.com",
            InternalDate = Now,
            FetchedAt = Now,
            UpdatedAt = Now,
        });
        db.Senders.Add(new SenderRow { Address = "news@example.com", Domain = "example.com", TotalCount = 1, UpdatedAt = Now });
        db.Jobs.Add(Job(JobStatus.Completed));
        db.AnalysisRuns.Add(new AnalysisRunRow
        {
            Id = runId,
            Scope = AnalysisScope.Messages,
            MessageIds = ["msg-1"],
            RequestedCount = 1,
            Status = AnalysisRunStatus.Completed,
            CreatedAt = Now,
        });
        db.Suggestions.Add(new SuggestionRow
        {
            Id = suggestionId,
            MessageId = "msg-1",
            RunId = runId,
            SenderAddress = "news@example.com",
            GroupKey = "news@example.com|weekly",
            TopicLabel = "Topic/Sub",
            Confidence = 0.9,
            Reason = "Synthetic reason",
            CreatedAt = Now,
        });
        db.Decisions.Add(new DecisionRow
        {
            Id = Guid.NewGuid(),
            MessageId = "msg-1",
            SenderAddress = "news@example.com",
            TopicLabel = "Topic/Sub",
            Outcome = DecisionOutcome.Approved,
            Embedding = new Vector(new float[] { 0.1f, 0.2f, 0.3f }),
            EmbeddingModel = "fake-embedding",
            CreatedAt = Now,
        });
        db.ActionBatches.Add(new ActionBatchRow { Id = batchId, Kind = ActionKind.ApplyRest, Description = "Synthetic batch", MessageCount = 1, CreatedAt = Now });
        db.ActionLog.Add(new ActionLogRow
        {
            Id = Guid.NewGuid(),
            BatchId = batchId,
            MessageId = "msg-1",
            SuggestionId = suggestionId,
            LabelsAdded = ["Topic/Sub"],
            LabelIdsBefore = ["INBOX"],
            LabelIdsAfter = ["Label_1"],
            CreatedAt = Now,
        });
        db.ExternalReviews.Add(new ExternalReviewRow
        {
            Id = Guid.NewGuid(),
            TargetType = ExternalReviewTarget.Suggestion,
            SuggestionId = suggestionId,
            SenderAddress = "news@example.com",
            Status = ExternalReviewStatus.Reviewed,
            CreatedAt = Now,
        });
        await db.SaveChangesAsync(Ct);
    }
}
