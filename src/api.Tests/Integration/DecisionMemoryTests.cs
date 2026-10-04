using System.Diagnostics;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    private static readonly string ShopScope = GroupKey.For(Message("s1", Shop, "Weekly offer 7", null));
    private static readonly string OtherTemplateScope = GroupKey.For(Message("s2", Shop, "Invoice ready", null));

    private readonly FakeEmbeddingGenerator embeddings = new();
    private readonly List<ServiceProvider> providers = [];
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

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in providers)
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task Approve_records_the_group_scope_and_the_background_pass_embeds_it_later()
    {
        var message = Message("m1", Shop, "Order 12345 shipped", "Your synthetic parcel is on its way", listId: " Orders.Example.COM ");
        await using (var db = postgres.CreateDbContext())
        {
            db.Messages.Add(message);
            await db.SaveChangesAsync(Ct);
        }

        var queue = new RecordingEmbeddingQueue();
        await using (var db = postgres.CreateDbContext())
        {
            var recorder = new DecisionRecorder(db, queue, new FakeTimeProvider(Now), NullLogger<DecisionRecorder>.Instance);
            await recorder.RecordAsync(Suggestion(message, "Shopping"), message, DecisionOutcome.Approved, Ct);
            await db.SaveChangesAsync(Ct);
            recorder.Committed();
            recorder.Committed(); // nothing new recorded: no second wake-up
        }

        queue.Notified.ShouldBe(1);
        embeddings.Inputs.ShouldBeEmpty();
        await using (var check = postgres.CreateDbContext())
        {
            var row = await check.Decisions.AsNoTracking().SingleAsync(Ct);
            (row.ListId, row.ScopeKey, row.Embedding).ShouldBe(("orders.example.com", GroupKey.For(message), null));
        }

        (await EmbeddingService().EmbedPendingAsync(Ct)).ShouldBe(1);

        var text = DecisionMemory.EmbeddingText(Shop, SubjectNormaliser.Template(message.Subject), message.Snippet);
        embeddings.Inputs.ShouldBe([text]);
        await using var after = postgres.CreateDbContext();
        var embedded = await after.Decisions.AsNoTracking().SingleAsync(Ct);
        embedded.EmbeddingModel.ShouldBe(EmbeddingModel);
        embedded.Embedding!.ToArray().ShouldBe(embeddings.Vector(text));
    }

    [Fact]
    public async Task Post_commit_failure_is_logged_not_thrown()
    {
        var message = Message("m1", Shop, "Order 12345 shipped", null);
        await using var db = postgres.CreateDbContext();
        var recorder = new DecisionRecorder(db, new RecordingEmbeddingQueue { Failure = new InvalidOperationException("synthetic") },
            new FakeTimeProvider(Now), NullLogger<DecisionRecorder>.Instance);
        await recorder.RecordAsync(Suggestion(message, "Shopping"), message, DecisionOutcome.Approved, Ct);

        Should.NotThrow(recorder.Committed);
    }

    [Fact]
    public async Task Embedding_failure_or_no_model_leaves_the_row_without_a_vector_until_a_later_pass()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Decisions.Add(Decision(Shop, "Shopping", DecisionOutcome.Approved, Now));
            await db.SaveChangesAsync(Ct);
        }

        embeddings.Failure = new HttpRequestException("synthetic outage");
        (await EmbeddingService().EmbedPendingAsync(Ct)).ShouldBe(0);

        embeddings.Failure = null;
        settings.Current = settings.Current with { EmbeddingModel = null };
        (await EmbeddingService().EmbedPendingAsync(Ct)).ShouldBe(0);
        embeddings.Inputs.ShouldBe([embeddings.Inputs[0], DecisionMemory.ProbeText]);
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Decisions.AsNoTracking().SingleAsync(Ct)).Embedding.ShouldBeNull();
        }

        settings.Current = settings.Current with { EmbeddingModel = EmbeddingModel };
        (await EmbeddingService().EmbedPendingAsync(Ct)).ShouldBe(1);
        (await EmbeddingService().EmbedPendingAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Decisions_purged_during_the_embed_call_end_the_pass_without_an_error()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Decisions.Add(Decision(Shop, "Shopping", DecisionOutcome.Approved, Now));
            await db.SaveChangesAsync(Ct);
        }

        // The hook runs inside the embed call, like a purge committing while Ollama answers.
        embeddings.Rejects = _ =>
        {
            using var purge = postgres.CreateDbContext();
            purge.Decisions.ExecuteDelete();
            return false;
        };

        (await EmbeddingService().EmbedPendingAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_rejected_decision_is_isolated_marked_and_retried_after_a_day_while_the_rest_embed()
    {
        var clock = new FakeTimeProvider(Now);
        var rows = Enumerable.Range(0, 7).Select(i => Decision($"s{i}@example.com", "Shopping", DecisionOutcome.Approved, Now.AddMinutes(i))).ToList();
        await using (var db = postgres.CreateDbContext())
        {
            db.Decisions.AddRange(rows);
            await db.SaveChangesAsync(Ct);
        }

        var poison = DecisionMemory.EmbeddingText(rows[3].SenderAddress, rows[3].SubjectTemplate, null);
        embeddings.Rejects = text => text == poison;
        (await EmbeddingService(clock).EmbedPendingAsync(Ct)).ShouldBe(6);
        await using (var db = postgres.CreateDbContext())
        {
            var stored = await db.Decisions.AsNoTracking().ToListAsync(Ct);
            stored.Where(d => d.Embedding is null).Select(d => (d.Id, d.EmbeddingFailedAt)).ShouldBe([(rows[3].Id, Now)]);
            stored.ShouldAllBe(d => d.Id == rows[3].Id || d.EmbeddingFailedAt == null);
        }

        var calls = embeddings.Inputs.Count;
        clock.Advance(DecisionEmbeddingService.FailedRetryInterval - TimeSpan.FromMinutes(1));
        (await EmbeddingService(clock).EmbedPendingAsync(Ct)).ShouldBe(0);
        embeddings.Inputs.Count.ShouldBe(calls);

        embeddings.Rejects = null;
        clock.Advance(TimeSpan.FromMinutes(2));
        (await EmbeddingService(clock).EmbedPendingAsync(Ct)).ShouldBe(1);
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Decisions.AsNoTracking().SingleAsync(d => d.Id == rows[3].Id, Ct)).EmbeddingFailedAt.ShouldBeNull();
        }
    }

    [Fact]
    public async Task An_embedder_outage_marks_no_decision()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Decisions.AddRange(Enumerable.Range(0, 4).Select(i => Decision($"s{i}@example.com", "Shopping", DecisionOutcome.Approved, Now)));
            await db.SaveChangesAsync(Ct);
        }

        embeddings.Failure = new HttpRequestException("synthetic outage");
        (await EmbeddingService().EmbedPendingAsync(Ct)).ShouldBe(0);
        embeddings.Inputs.ShouldBe([.. embeddings.Inputs.Take(4), DecisionMemory.ProbeText]);
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Decisions.AsNoTracking().ToListAsync(Ct)).ShouldAllBe(d => d.Embedding == null && d.EmbeddingFailedAt == null);
        }
    }

    [Fact]
    public async Task A_batch_the_embedder_rejects_entirely_while_the_probe_works_marks_every_decision()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Decisions.AddRange(Enumerable.Range(0, 4).Select(i => Decision($"s{i}@example.com", "Shopping", DecisionOutcome.Approved, Now)));
            await db.SaveChangesAsync(Ct);
        }

        embeddings.Rejects = text => text != DecisionMemory.ProbeText;
        (await EmbeddingService(new FakeTimeProvider(Now)).EmbedPendingAsync(Ct)).ShouldBe(0);
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Decisions.AsNoTracking().ToListAsync(Ct)).ShouldAllBe(d => d.Embedding == null && d.EmbeddingFailedAt == Now);
        }
    }

    [Fact]
    public async Task A_lone_rejected_decision_is_marked_once_and_restamped_after_the_daily_retry()
    {
        var clock = new FakeTimeProvider(Now);
        var row = Decision(Shop, "Shopping", DecisionOutcome.Approved, Now);
        await using (var db = postgres.CreateDbContext())
        {
            db.Decisions.Add(row);
            await db.SaveChangesAsync(Ct);
        }

        embeddings.Rejects = text => text != DecisionMemory.ProbeText;
        (await EmbeddingService(clock).EmbedPendingAsync(Ct)).ShouldBe(0);
        embeddings.Inputs.Count.ShouldBe(2);
        (await FailedAt(row.Id)).ShouldBe(Now);

        clock.Advance(DecisionEmbeddingService.FailedRetryInterval - TimeSpan.FromMinutes(1));
        (await EmbeddingService(clock).EmbedPendingAsync(Ct)).ShouldBe(0);
        embeddings.Inputs.Count.ShouldBe(2);
        (await FailedAt(row.Id)).ShouldBe(Now);

        clock.Advance(TimeSpan.FromMinutes(2));
        (await EmbeddingService(clock).EmbedPendingAsync(Ct)).ShouldBe(0);
        embeddings.Inputs.Count.ShouldBe(4);
        (await FailedAt(row.Id)).ShouldBe(clock.GetUtcNow());
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

        var memory = Memory(db);
        var vectors = await memory.EmbedMessagesAsync([query, other, query], Ct);
        var hints = await memory.FindSimilarAsync([query, other], vectors, DecisionMemory.DefaultSimilarCount, Ct);

        embeddings.Inputs.Count.ShouldBe(2);
        hints.Select(h => (h.TopicLabel, h.Outcome)).ShouldBe([("Shopping", "approved"), ("Offers", "rejected")]);
        hints[0].Similarity.ShouldBe(1, 1e-5);
        hints[1].Similarity.ShouldBe(1.0); // exact-sender fill
    }

    [Fact]
    public async Task With_llm_fake_vectors_are_recorded_under_the_fake_model_not_the_settings_model()
    {
        var message = Message("f1", Shop, "Weekly offer 7", "Synthetic deals");
        await using var db = postgres.CreateDbContext();
        db.Messages.Add(message);
        await db.SaveChangesAsync(Ct);
        var factory = new LlmClientFactory(
            new StubHttpClientFactory(new StubOllamaHandler()), settings, Options.Create(new LlmOptions { UseFake = true }));
        var memory = new DecisionMemory(db, factory, settings, NullLogger<DecisionMemory>.Instance);
        var decision = Decision(Shop, "Shopping", DecisionOutcome.Approved, Now);

        await memory.EmbedAsync([decision], Ct);
        var vectors = await memory.EmbedMessagesAsync([message], Ct);

        decision.EmbeddingModel.ShouldBe(FakeOllamaCatalog.EmbeddingModel);
        vectors!.Model.ShouldBe(FakeOllamaCatalog.EmbeddingModel);
        settings.Current.EmbeddingModel.ShouldBe(EmbeddingModel);
    }

    [Fact]
    public async Task Without_an_embedding_model_retrieval_falls_back_to_the_sender_and_the_normalised_list()
    {
        settings.Current = settings.Current with { EmbeddingModel = null };
        var query = Message("q1", Shop, "Weekly offer 7", null, listId: " Offers.Example.COM ");
        await using var db = postgres.CreateDbContext();
        var listOnly = Decision("lists@example.com", "Lists", DecisionOutcome.Approved, Now);
        listOnly.ListId = "offers.example.com";
        db.Decisions.AddRange(
            Decision(Shop, "Shopping", DecisionOutcome.Approved, Now.AddDays(-1)),
            Decision(Shop, "Shopping", DecisionOutcome.Approved, Now.AddDays(-2)),
            listOnly,
            Decision(News, "News", DecisionOutcome.Approved, Now));
        await db.SaveChangesAsync(Ct);

        var memory = Memory(db);
        var hints = await memory.FindSimilarAsync([query], await memory.EmbedMessagesAsync([query], Ct), DecisionMemory.DefaultSimilarCount, Ct);

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

        var memory = Memory(db);
        (await memory.FindSimilarAsync([query], await memory.EmbedMessagesAsync([query], Ct), 5, Ct)).ShouldBeEmpty();

        settings.Current = settings.Current with { EmbeddingModel = "previous-embedding-model" };
        (await memory.FindSimilarAsync([query], await memory.EmbedMessagesAsync([query], Ct), 5, Ct))
            .ShouldHaveSingleItem().TopicLabel.ShouldBe("News");
    }

    [Theory]
    [InlineData("consistent", 3)]
    [InlineData("below N", null)]
    [InlineData("mixed outcomes", null)]
    [InlineData("newer rejection in scope", null)]
    [InlineData("newer rejection in another template", 3)]
    [InlineData("derived and memory approvals", null)]
    [InlineData("edited derived approval", 3)]
    [InlineData("sender-pattern approvals, edited or not", null)]
    [InlineData("same message twice", null)]
    public async Task Pattern_needs_N_consistent_human_approvals_of_distinct_messages_in_the_group_scope(string scenario, int? expected)
    {
        await using var db = postgres.CreateDbContext();
        DecisionRow Approval(int day, string label = "Shopping", SuggestionSource source = SuggestionSource.Llm, bool edited = false,
            string? messageId = null, string? scope = null)
        {
            var d = Decision(Shop, label, DecisionOutcome.Approved, Now.AddDays(day), scopeKey: scope ?? ShopScope, source: source);
            d.Edited = edited;
            d.MessageId = messageId ?? $"m{day}";
            return d;
        }

        db.Decisions.AddRange(Approval(-10), Approval(-9));
        // An older rejection in scope never blocks the pattern.
        db.Decisions.Add(Decision(Shop, "Shopping", DecisionOutcome.Rejected, Now.AddDays(-30), scopeKey: ShopScope));
        db.Decisions.AddRange(scenario switch
        {
            "consistent" => [Approval(-8)],
            "below N" => [],
            "mixed outcomes" => [Approval(-8), Approval(-20, label: "Receipts")],
            "newer rejection in scope" => [Approval(-8), Decision(Shop, "Shopping", DecisionOutcome.Rejected, Now, scopeKey: ShopScope)],
            "newer rejection in another template" =>
                [Approval(-8), Decision(Shop, "Shopping", DecisionOutcome.Rejected, Now, scopeKey: OtherTemplateScope)],
            "derived and memory approvals" =>
                [Approval(-8, source: SuggestionSource.Derived), Approval(-7, source: SuggestionSource.Memory)],
            "edited derived approval" => [Approval(-8, source: SuggestionSource.Derived, edited: true)],
            "sender-pattern approvals, edited or not" =>
                [Approval(-8, source: SuggestionSource.SenderPattern), Approval(-7, source: SuggestionSource.SenderPattern, edited: true)],
            "same message twice" => [Approval(-8, messageId: "m-10")],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        });
        await db.SaveChangesAsync(Ct);

        var patterns = await Memory(db).FindPatternsAsync([ShopScope, OtherTemplateScope], minApprovals: 3, Ct);

        patterns.ShouldNotContainKey(OtherTemplateScope);
        if (expected is { } approvals)
        {
            var pattern = patterns[ShopScope];
            (pattern.TopicLabel, pattern.Approvals, pattern.Agreement).ShouldBe(("Shopping", approvals, 1.0));
        }
        else
        {
            patterns.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Pattern_uses_the_callers_minimum()
    {
        await using var db = postgres.CreateDbContext();
        db.Decisions.AddRange(
            Decision(Shop, "Shopping", DecisionOutcome.Approved, Now, scopeKey: ShopScope),
            Decision(Shop, "Shopping", DecisionOutcome.Approved, Now.AddDays(-1), scopeKey: ShopScope));
        await db.SaveChangesAsync(Ct);

        (await Memory(db).FindPatternsAsync([ShopScope], minApprovals: 3, Ct)).ShouldBeEmpty();
        (await Memory(db).FindPatternsAsync([ShopScope], minApprovals: 2, Ct)).ShouldContainKey(ShopScope);
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

    private DecisionEmbeddingService EmbeddingService(TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => postgres.CreateDbContext());
        services.AddScoped<IDecisionMemory>(sp => Memory(sp.GetRequiredService<AppDbContext>()));
        providers.Add(services.BuildServiceProvider());
        return new DecisionEmbeddingService(
            providers[^1].GetRequiredService<IServiceScopeFactory>(), new DecisionEmbeddingQueue(), clock ?? TimeProvider.System,
            NullLogger<DecisionEmbeddingService>.Instance);
    }

    private async Task<DateTimeOffset?> FailedAt(Guid id)
    {
        await using var db = postgres.CreateDbContext();
        return (await db.Decisions.AsNoTracking().SingleAsync(d => d.Id == id, Ct)).EmbeddingFailedAt;
    }

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
        string sender, string label, DecisionOutcome outcome, DateTimeOffset at, string? template = "weekly offer #",
        string? scopeKey = null, SuggestionSource source = SuggestionSource.Llm) => new()
        {
            Id = Guid.NewGuid(),
            SenderAddress = sender,
            SubjectTemplate = template,
            ScopeKey = scopeKey,
            TopicLabel = label,
            Outcome = outcome,
            Source = source,
            CreatedAt = at,
        };
}
