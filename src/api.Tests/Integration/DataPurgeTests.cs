using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class DataPurgeTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private const string SettingsDocument = """{"chatModel": "chat-model-a"}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE settings, oauth_tokens, jobs, external_reviews, action_log, action_batches, decisions, suggestions, analysis_runs, fetch_run_messages, messages, senders", Ct);
        await postgres.ResetFetchStateAsync();
    }

    public async ValueTask DisposeAsync()
    {
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
            // Paused: the test host's job runner never claims it.
            db.Jobs.Add(Job(JobStatus.Paused));
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

        var response = await factory.CreateClient().PostAsJsonAsync("/api/settings/purge", new { confirm = "purge" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await using var db = postgres.CreateDbContext();
        (await db.Messages.CountAsync(Ct)).ShouldBe(1);
    }

    private async Task<PurgeResult> PurgeAsync()
    {
        await using var db = postgres.CreateDbContext();
        using var labels = new LabelCatalog(factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        return await new DataPurgeService(db, labels, NullLogger<DataPurgeService>.Instance).PurgeAsync(Ct);
    }

    private async Task<HttpResponseMessage> PostAsync(object body)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiRequestGuardMiddleware.RequestedWithHeader, "XMLHttpRequest");
        return await client.PostAsJsonAsync("/api/settings/purge", body, Ct);
    }

    private static JobRow Job(JobStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Type = "synthetic-job",
        Queue = "synthetic-queue",
        Status = status,
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
