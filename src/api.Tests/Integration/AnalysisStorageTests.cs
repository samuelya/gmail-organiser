using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class AnalysisStorageTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.ActionBatches.ExecuteDeleteAsync(Ct);
        await db.Decisions.ExecuteDeleteAsync(Ct);
        await db.Suggestions.ExecuteDeleteAsync(Ct);
        await db.AnalysisRuns.ExecuteDeleteAsync(Ct);
        await db.Messages.ExecuteDeleteAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Every_table_round_trips()
    {
        var runId = Guid.NewGuid();
        var suggestionId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        await using (var db = postgres.CreateDbContext())
        {
            db.Messages.Add(Message("msg-1", AnalysisStatus.Approved));
            db.AnalysisRuns.Add(new AnalysisRunRow
            {
                Id = runId,
                Scope = AnalysisScope.Messages,
                MessageIds = ["msg-1"],
                RequestedCount = 1,
                GroupingMode = AnalysisGroupingMode.SenderSubject,
                Status = AnalysisRunStatus.Completed,
                MessagesCovered = 1,
                MessagesFromMemory = 1,
                CreatedAt = Now,
            });
            db.Suggestions.Add(new SuggestionRow
            {
                Id = suggestionId,
                MessageId = "msg-1",
                RunId = runId,
                SenderAddress = "news@example.com",
                GroupKey = "news@example.com|weekly",
                Source = SuggestionSource.SenderPattern,
                TopicLabel = "Topic/Sub",
                Confidence = 0.75f,
                Reason = "Synthetic reason",
                FilterCriteria = """{"from":"news@example.com"}""",
                Status = SuggestionStatus.Approved,
                CreatedAt = Now,
            });
            db.Decisions.Add(new DecisionRow
            {
                Id = Guid.NewGuid(),
                MessageId = "msg-1",
                SenderAddress = "news@example.com",
                TopicLabel = "Topic/Sub",
                Outcome = DecisionOutcome.Approved,
                Source = SuggestionSource.Memory,
                Embedding = new Vector(new float[] { 0.1f, 0.2f, 0.3f }),
                EmbeddingModel = "fake-embedding",
                CreatedAt = Now,
            });
            db.ActionBatches.Add(new ActionBatchRow
            {
                Id = batchId, Kind = ActionKind.ApplyRest, Description = "Synthetic batch", MessageCount = 1, CreatedAt = Now,
            });
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
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = postgres.CreateDbContext())
        {
            (await db.Messages.AsNoTracking().SingleAsync(Ct)).AnalysisStatus.ShouldBe(AnalysisStatus.Approved);
            var run = await db.AnalysisRuns.AsNoTracking().SingleAsync(Ct);
            run.Scope.ShouldBe(AnalysisScope.Messages);
            run.MessageIds.ShouldBe(["msg-1"]);
            run.GroupingMode.ShouldBe(AnalysisGroupingMode.SenderSubject);
            run.Status.ShouldBe(AnalysisRunStatus.Completed);
            run.MessagesFromMemory.ShouldBe(1);
            var suggestion = await db.Suggestions.AsNoTracking().SingleAsync(Ct);
            suggestion.RunId.ShouldBe(runId);
            suggestion.Source.ShouldBe(SuggestionSource.SenderPattern);
            suggestion.Confidence.ShouldBe(0.75f);
            suggestion.FilterCriteria.ShouldNotBeNull().ShouldContain("news@example.com");
            var decision = await db.Decisions.AsNoTracking().SingleAsync(Ct);
            decision.Embedding.ShouldNotBeNull().ToArray().ShouldBe([0.1f, 0.2f, 0.3f]);
            decision.Outcome.ShouldBe(DecisionOutcome.Approved);
            (await db.ActionBatches.AsNoTracking().SingleAsync(Ct)).Kind.ShouldBe(ActionKind.ApplyRest);
            var log = await db.ActionLog.AsNoTracking().SingleAsync(Ct);
            log.LabelIdsAfter.ShouldBe(["Label_1"]);
            log.LabelsRemoved.ShouldBeEmpty();
        }

        var stored = await RawAsync($"SELECT scope || ',' || grouping_mode || ',' || status AS \"Value\" FROM analysis_runs");
        stored.ShouldBe("messages,sender_subject,completed");
        (await RawAsync($"SELECT source AS \"Value\" FROM suggestions")).ShouldBe("sender_pattern");
        (await RawAsync($"SELECT kind AS \"Value\" FROM action_batches")).ShouldBe("apply_rest");
    }

    [Fact]
    public async Task Embeddings_of_different_dimensions_share_the_column()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Decisions.AddRange(Decision(new Vector(new float[] { 1, 2 })), Decision(new Vector(new float[] { 1, 2, 3, 4 })), Decision(null));
            await db.SaveChangesAsync(Ct);
        }

        await using var read = postgres.CreateDbContext();
        var dims = await read.Decisions.AsNoTracking().Select(d => d.Embedding == null ? 0 : d.Embedding.ToArray().Length).ToListAsync(Ct);
        dims.Order().ShouldBe([0, 2, 4]);
    }

    [Fact]
    public async Task One_suggestion_per_message_and_cascades()
    {
        var runId = Guid.NewGuid();
        await using (var db = postgres.CreateDbContext())
        {
            db.Messages.Add(Message("msg-1", AnalysisStatus.Analysed));
            db.AnalysisRuns.Add(new AnalysisRunRow { Id = runId, Scope = AnalysisScope.Inbox, CreatedAt = Now });
            db.Suggestions.Add(Suggestion("msg-1", runId));
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = postgres.CreateDbContext())
        {
            db.Suggestions.Add(Suggestion("msg-1", runId));
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }

        await using (var db = postgres.CreateDbContext())
        {
            await db.AnalysisRuns.ExecuteDeleteAsync(Ct);
            (await db.Suggestions.AsNoTracking().SingleAsync(Ct)).RunId.ShouldBeNull();
            await db.Messages.ExecuteDeleteAsync(Ct);
            (await db.Suggestions.CountAsync(Ct)).ShouldBe(0);
        }
    }

    private async Task<string> RawAsync(FormattableString sql)
    {
        await using var db = postgres.CreateDbContext();
        return await db.Database.SqlQuery<string>(sql).SingleAsync(Ct);
    }

    private static MessageRow Message(string id, AnalysisStatus status) => new()
    {
        Id = id,
        ThreadId = "thread-1",
        FromAddress = "news@example.com",
        InternalDate = Now,
        AnalysisStatus = status,
        FetchedAt = Now,
        UpdatedAt = Now,
    };

    private static SuggestionRow Suggestion(string messageId, Guid runId) => new()
    {
        Id = Guid.NewGuid(),
        MessageId = messageId,
        RunId = runId,
        SenderAddress = "news@example.com",
        TopicLabel = "Topic",
        Reason = "Synthetic reason",
        CreatedAt = Now,
    };

    private static DecisionRow Decision(Vector? embedding) => new()
    {
        Id = Guid.NewGuid(),
        SenderAddress = "news@example.com",
        TopicLabel = "Topic",
        Embedding = embedding,
        CreatedAt = Now,
    };
}
