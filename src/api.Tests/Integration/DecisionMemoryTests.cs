using System.Diagnostics;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class DecisionMemoryTests(PostgresFixture postgres, ITestOutputHelper output) : IAsyncLifetime
{
    private const string EmbeddingModel = "fake-embedding-model";
    private const string Shop = "shop@example.com";
    private const string News = "news@example.com";
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeEmbeddingGenerator embeddings = new();
    private readonly InMemorySettingsStore settings = new(new AppSettings
    {
        OllamaBaseUrl = "http://ollama.example.com:11434",
        EmbeddingModel = EmbeddingModel,
    });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Decisions.ExecuteDeleteAsync(Ct);
        await db.Suggestions.ExecuteDeleteAsync(Ct);
        await db.AnalysisRuns.ExecuteDeleteAsync(Ct);
        await db.Messages.ExecuteDeleteAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Approve_embeds_sender_subject_template_and_snippet_after_the_commit()
    {
        var message = Message("m1", Shop, "Order 12345 shipped", "Your synthetic parcel is on its way");
        await using (var db = postgres.CreateDbContext())
        {
            db.Messages.Add(message);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = postgres.CreateDbContext())
        {
            var recorder = new DecisionRecorder(db, Memory(db), new FakeTimeProvider(Now));
            await recorder.RecordAsync(Suggestion(message, "Shopping"), message, DecisionOutcome.Approved, Ct);
            await db.SaveChangesAsync(Ct);
            embeddings.Inputs.ShouldBeEmpty();
            await recorder.EmbedRecordedAsync(Ct);
        }

        var text = DecisionMemory.EmbeddingText(Shop, SubjectNormaliser.Template(message.Subject), message.Snippet);
        embeddings.Inputs.ShouldBe([text]);
        await using var check = postgres.CreateDbContext();
        var row = await check.Decisions.AsNoTracking().SingleAsync(Ct);
        row.EmbeddingModel.ShouldBe(EmbeddingModel);
        row.Embedding!.ToArray().ShouldBe(embeddings.Vector(text));
    }

    [Fact]
    public async Task Embedding_failure_or_no_model_leaves_the_row_without_a_vector()
    {
        await using var db = postgres.CreateDbContext();
        var row = Decision(Shop, "Shopping", DecisionOutcome.Approved, Now);

        embeddings.Failure = new HttpRequestException("synthetic outage");
        await Memory(db).EmbedAsync([row], Ct);
        (row.Embedding, row.EmbeddingModel).ShouldBe((null, null));

        embeddings.Failure = null;
        settings.Current = settings.Current with { EmbeddingModel = null };
        await Memory(db).EmbedAsync([row], Ct);
        (row.Embedding, row.EmbeddingModel).ShouldBe((null, null));
        embeddings.Inputs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Similar_retrieval_ranks_the_same_sender_decision_first_and_embeds_all_messages_in_one_call()
    {
        var query = Message("q1", Shop, "Weekly offer 7", "Synthetic deals of the week");
        var other = Message("q2", "unknown@example.com", "Hello", null);
        await using var db = postgres.CreateDbContext();
        var similar = Decision(Shop, "Shopping", DecisionOutcome.Approved, Now.AddDays(-3));
        similar.Embedding = new Vector(embeddings.Vector(TextOf(query)));
        similar.EmbeddingModel = EmbeddingModel;
        var older = Decision(Shop, "Offers", DecisionOutcome.Rejected, Now.AddDays(-5));
        var unrelated = Decision(News, "News", DecisionOutcome.Approved, Now);
        unrelated.Embedding = new Vector(embeddings.Vector("news@example.com | issue | synthetic"));
        unrelated.EmbeddingModel = EmbeddingModel;
        db.Decisions.AddRange(similar, older, unrelated);
        await db.SaveChangesAsync(Ct);

        var hints = await Memory(db).FindSimilarAsync([query, other], DecisionMemory.DefaultSimilarCount, Ct);

        embeddings.Inputs.Count.ShouldBe(2);
        hints.Select(h => (h.TopicLabel, h.Outcome)).ShouldBe([("Shopping", "approved"), ("Offers", "rejected")]);
        hints[0].Similarity.ShouldBe(1, 1e-5);
        hints[1].Similarity.ShouldBe(1.0); // exact-sender fill
    }

    [Fact]
    public async Task Without_an_embedding_model_retrieval_falls_back_to_the_sender_and_list()
    {
        settings.Current = settings.Current with { EmbeddingModel = null };
        var query = Message("q1", Shop, "Weekly offer 7", null, listId: "offers.example.com");
        await using var db = postgres.CreateDbContext();
        var listOnly = Decision("lists@example.com", "Lists", DecisionOutcome.Approved, Now);
        listOnly.ListId = "offers.example.com";
        db.Decisions.AddRange(
            Decision(Shop, "Shopping", DecisionOutcome.Approved, Now.AddDays(-1)),
            Decision(Shop, "Shopping", DecisionOutcome.Approved, Now.AddDays(-2)),
            listOnly,
            Decision(News, "News", DecisionOutcome.Approved, Now));
        await db.SaveChangesAsync(Ct);

        var hints = await Memory(db).FindSimilarAsync([query], DecisionMemory.DefaultSimilarCount, Ct);

        embeddings.Inputs.ShouldBeEmpty();
        hints.Select(h => (h.TopicLabel, h.Similarity)).ShouldBe([("Shopping", 1.0), ("Lists", DecisionMemory.ListMatchSimilarity)]);
    }

    [Fact]
    public async Task Decisions_of_another_embedding_model_are_ignored()
    {
        var query = Message("q1", Shop, "Weekly offer 7", "Synthetic deals");
        await using var db = postgres.CreateDbContext();
        var stale = Decision(News, "News", DecisionOutcome.Approved, Now);
        stale.Embedding = new Vector(embeddings.Vector(TextOf(query)));
        stale.EmbeddingModel = "previous-embedding-model";
        db.Decisions.Add(stale);
        await db.SaveChangesAsync(Ct);

        (await Memory(db).FindSimilarAsync([query], 5, Ct)).ShouldBeEmpty();

        settings.Current = settings.Current with { EmbeddingModel = "previous-embedding-model" };
        (await Memory(db).FindSimilarAsync([query], 5, Ct)).ShouldHaveSingleItem().TopicLabel.ShouldBe("News");
    }

    [Theory]
    [InlineData(3, 0, false, "Shopping")] // three consistent approvals
    [InlineData(2, 0, false, null)] // below N
    [InlineData(3, 1, false, null)] // 3 of 4 = 75 % < 80 %
    [InlineData(4, 1, false, "Shopping")] // 4 of 5 = 80 %
    [InlineData(3, 0, true, null)] // a newer rejection of the same subject template
    public async Task Sender_pattern_needs_N_consistent_approvals_and_no_newer_rejection(
        int agreeing, int disagreeing, bool newerRejection, string? expected)
    {
        await using var db = postgres.CreateDbContext();
        var template = SubjectNormaliser.Template("Weekly offer 7");
        for (var i = 0; i < agreeing; i++)
        {
            db.Decisions.Add(Decision(Shop, "Shopping", DecisionOutcome.Approved, Now.AddDays(-10 + i), template));
        }

        for (var i = 0; i < disagreeing; i++)
        {
            db.Decisions.Add(Decision(Shop, "Receipts", DecisionOutcome.Approved, Now.AddDays(-20 + i), template));
        }

        // Older rejections and rejections of another template never block the pattern.
        db.Decisions.Add(Decision(Shop, "Shopping", DecisionOutcome.Rejected, Now.AddDays(-30), template));
        db.Decisions.Add(Decision(Shop, "Shopping", DecisionOutcome.Rejected, Now, "another template"));
        if (newerRejection)
        {
            db.Decisions.Add(Decision(Shop, "Shopping", DecisionOutcome.Rejected, Now, template));
        }

        await db.SaveChangesAsync(Ct);

        var pattern = await Memory(db).FindSenderPatternAsync(Shop, null, template, Ct);

        pattern?.TopicLabel.ShouldBe(expected);
        if (pattern is not null)
        {
            (pattern.Approvals, pattern.Agreement).ShouldBe((agreeing, (double)agreeing / (agreeing + disagreeing)));
        }
    }

    /// <summary>
    /// Design check measurement for #111: an exact cosine top-5 scan over 20 000 decisions of 768 dimensions. Explicit
    /// (run with <c>--explicit only</c>), not part of CI.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Measure_top5_cosine_scan_over_20000_decisions_of_768_dimensions()
    {
        var generator = new FakeEmbeddingGenerator(768);
        await using (var db = postgres.CreateDbContext())
        {
            for (var batch = 0; batch < 10; batch++)
            {
                for (var i = 0; i < 2000; i++)
                {
                    var n = (batch * 2000) + i;
                    var row = Decision($"sender{n % 500}@example.com", $"Label {n % 20}", DecisionOutcome.Approved, Now.AddMinutes(-n));
                    row.Embedding = new Vector(generator.Vector($"synthetic decision {n}"));
                    row.EmbeddingModel = EmbeddingModel;
                    db.Decisions.Add(row);
                }

                await db.SaveChangesAsync(Ct);
                db.ChangeTracker.Clear();
            }

            await db.Database.ExecuteSqlRawAsync("ANALYZE decisions", Ct);
        }

        var query = new Vector(generator.Vector("synthetic query"));
        var timings = new List<double>();
        await using (var db = postgres.CreateDbContext())
        {
            for (var run = 0; run < 6; run++)
            {
                var watch = Stopwatch.StartNew();
                var top = await db.Decisions.AsNoTracking()
                    .Where(d => d.EmbeddingModel == EmbeddingModel && d.Embedding != null)
                    .Select(d => new { d.Id, Distance = d.Embedding!.CosineDistance(query) })
                    .OrderBy(x => x.Distance)
                    .Take(5)
                    .ToListAsync(Ct);
                watch.Stop();
                top.Count.ShouldBe(5);
                timings.Add(watch.Elapsed.TotalMilliseconds);
            }
        }

        var warm = timings.Skip(1).Order().ToList();
        output.WriteLine($"cold {timings[0]:F1} ms; warm median {warm[warm.Count / 2]:F1} ms, max {warm[^1]:F1} ms");
    }

    private DecisionMemory Memory(AppDbContext db) =>
        new(db, new FakeLlmClientFactory(embed: embeddings), settings, NullLogger<DecisionMemory>.Instance);

    private static string TextOf(MessageRow m) =>
        DecisionMemory.EmbeddingText(m.FromAddress, SubjectNormaliser.Template(m.Subject), m.Snippet);

    private static MessageRow Message(string id, string from, string subject, string? snippet, string? listId = null) => new()
    {
        Id = id,
        ThreadId = $"t-{id}",
        FromAddress = from,
        Subject = subject,
        Snippet = snippet,
        ListId = listId,
        InternalDate = Now,
        FetchedAt = Now,
        UpdatedAt = Now,
    };

    private static SuggestionRow Suggestion(MessageRow message, string label) => new()
    {
        Id = Guid.NewGuid(),
        MessageId = message.Id,
        SenderAddress = message.FromAddress,
        Source = SuggestionSource.Llm,
        TopicLabel = label,
        Confidence = 0.9,
        Reason = "Synthetic reason",
        CreatedAt = Now,
    };

    private static DecisionRow Decision(
        string sender, string label, DecisionOutcome outcome, DateTimeOffset at, string? template = "weekly offer #") => new()
    {
        Id = Guid.NewGuid(),
        SenderAddress = sender,
        SubjectTemplate = template,
        TopicLabel = label,
        Outcome = outcome,
        Source = SuggestionSource.Llm,
        CreatedAt = at,
    };
}
