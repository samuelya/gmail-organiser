using System.Diagnostics;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Memory;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class EmbeddingIndexTests(PostgresFixture postgres, ITestOutputHelper output) : IAsyncLifetime
{
    private const string EmbeddingModel = "fake-embedding-model";
    private const int Dimension = 64;
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemorySettingsStore settings = new(new AppSettings
    {
        OllamaBaseUrl = "http://ollama.example.com:11434",
        EmbeddingModel = EmbeddingModel,
    });

    private readonly List<ServiceProvider> providers = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await ResetAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in providers)
        {
            await provider.DisposeAsync();
        }

        await ResetAsync();
    }

    [Fact]
    public async Task The_embedding_pass_creates_the_index_for_the_model_dimension_and_a_dimension_change_swaps_it()
    {
        await using var db = postgres.CreateDbContext();
        db.Decisions.Add(Decision("a@example.com", "Label A"));
        await db.SaveChangesAsync(Ct);

        (await EmbeddingService(new FakeEmbeddingGenerator(16)).EmbedPendingAsync(Ct)).ShouldBe(1);
        (await IndexesAsync(db)).ShouldBe([("ix_decisions_embedding_hnsw_16", true)]);

        // An earlier model's rows stay behind with another dimension; the partial index skips them.
        var second = Decision("b@example.com", "Label B");
        second.CreatedAt = Now.AddMinutes(1);
        db.Decisions.Add(second);
        await db.SaveChangesAsync(Ct);
        (await EmbeddingService(new FakeEmbeddingGenerator(24)).EmbedPendingAsync(Ct)).ShouldBe(1);
        (await IndexesAsync(db)).ShouldBe([("ix_decisions_embedding_hnsw_24", true)]);
    }

    [Fact]
    public async Task Maintenance_is_skipped_while_another_session_holds_the_lock()
    {
        await using var holder = postgres.CreateDbContext();
        await holder.Database.OpenConnectionAsync(Ct);
        await holder.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(hashtext('embedding_index:decisions'))", Ct);
        await using var db = postgres.CreateDbContext();

        await MemoryTestFactory.Indexes(db).EnsureAsync(8, Ct);
        (await IndexesAsync(db)).ShouldBeEmpty();

        await holder.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(hashtext('embedding_index:decisions'))", Ct);
        await MemoryTestFactory.Indexes(db).EnsureAsync(8, Ct);
        (await IndexesAsync(db)).ShouldBe([("ix_decisions_embedding_hnsw_8", true)]);
    }

    [Fact]
    public async Task An_invalid_index_left_by_a_failed_build_is_rebuilt()
    {
        await using var db = postgres.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX ix_decisions_embedding_hnsw_8 ON decisions USING hnsw ((embedding::vector(8)) vector_cosine_ops) WHERE vector_dims(embedding) = 8",
            Ct);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE pg_index SET indisvalid = false WHERE indexrelid = 'ix_decisions_embedding_hnsw_8'::regclass", Ct);

        await MemoryTestFactory.Indexes(db).EnsureAsync(8, Ct);

        (await IndexesAsync(db)).ShouldBe([("ix_decisions_embedding_hnsw_8", true)]);
    }

    [Fact]
    public async Task Similarity_query_uses_the_index_on_2000_decisions_and_returns_what_a_scan_returns()
    {
        var generator = new FakeEmbeddingGenerator(Dimension);
        await using (var db = postgres.CreateDbContext())
        {
            for (var n = 0; n < 2000; n++)
            {
                var row = Decision($"sender{n % 200}@example.com", $"Label {n % 40}");
                row.Embedding = new Vector(Near(generator, $"centre {n % 40}", $"noise {n}"));
                row.EmbeddingModel = EmbeddingModel;
                db.Decisions.Add(row);
            }

            await db.SaveChangesAsync(Ct);
            await db.Database.ExecuteSqlRawAsync("ANALYZE decisions", Ct);
        }

        var message = Query();
        var query = new Vector(Near(generator, "centre 7", "query"));
        var vectors = new MessageVectors(EmbeddingModel, new Dictionary<string, Vector> { [message.Id] = query });

        await using var scanDb = postgres.CreateDbContext();
        var scanned = await Memory(scanDb, generator).FindSimilarAsync([message], vectors, 5, null, [], Ct);
        (await PlanAsync(scanDb, query)).ShouldNotContain("ix_decisions_embedding_hnsw");

        await using var indexedDb = postgres.CreateDbContext();
        await MemoryTestFactory.Indexes(indexedDb).EnsureAsync(Dimension, Ct);
        var plan = await PlanAsync(indexedDb, query);
        output.WriteLine(plan);
        plan.ShouldContain($"Index Scan using ix_decisions_embedding_hnsw_{Dimension}");
        var indexed = await Memory(indexedDb, generator).FindSimilarAsync([message], vectors, 5, null, [], Ct);

        scanned.ShouldNotBeEmpty();
        indexed.Select(h => (h.SenderAddress, h.TopicLabel, Math.Round(h.Similarity, 5)))
            .ShouldBe(scanned.Select(h => (h.SenderAddress, h.TopicLabel, Math.Round(h.Similarity, 5))));
    }

    [Fact]
    public async Task With_the_index_another_models_rows_and_excluded_messages_nearest_the_query_still_leave_k_hits()
    {
        var generator = new FakeEmbeddingGenerator(Dimension);
        const string OldModel = "fake-embedding-model-old";
        var excluded = Enumerable.Range(0, 200).Select(n => $"run-{n}").ToArray();
        await using (var db = postgres.CreateDbContext())
        {
            // Nearest of all: an earlier model's rows of the same dimension and this run's own decisions, both filtered out.
            for (var n = 0; n < 2000; n++)
            {
                var row = Decision($"old{n % 100}@example.com", $"Old {n % 10}");
                row.Embedding = new Vector(Near(generator, "centre 7", $"old {n}", 0.05f));
                row.EmbeddingModel = n < 1800 ? OldModel : EmbeddingModel;
                row.MessageId = n < 1800 ? null : excluded[n - 1800];
                db.Decisions.Add(row);
            }

            for (var n = 0; n < 5; n++)
            {
                var row = Decision($"kept{n}@example.com", $"Kept {n}");
                row.Embedding = new Vector(Near(generator, "centre 7", $"kept {n}", 0.3f));
                row.EmbeddingModel = EmbeddingModel;
                db.Decisions.Add(row);
            }

            await db.SaveChangesAsync(Ct);
            await db.Database.ExecuteSqlRawAsync("ANALYZE decisions", Ct);
        }

        var message = Query();
        var vectors = new MessageVectors(EmbeddingModel, new Dictionary<string, Vector> { [message.Id] = new(Near(generator, "centre 7", "query")) });
        await using var scanDb = postgres.CreateDbContext();
        var scanned = await Memory(scanDb, generator).FindSimilarAsync([message], vectors, 5, null, excluded, Ct);

        await using var indexedDb = postgres.CreateDbContext();
        await MemoryTestFactory.Indexes(indexedDb).EnsureAsync(Dimension, Ct);
        var indexed = await Memory(indexedDb, generator).FindSimilarAsync([message], vectors, 5, null, excluded, Ct);

        scanned.Select(h => h.TopicLabel).Order().ShouldBe(["Kept 0", "Kept 1", "Kept 2", "Kept 3", "Kept 4"]);
        indexed.Select(h => (h.SenderAddress, h.TopicLabel, Math.Round(h.Similarity, 5)))
            .ShouldBe(scanned.Select(h => (h.SenderAddress, h.TopicLabel, Math.Round(h.Similarity, 5))));
    }

    [Fact]
    public async Task A_query_vector_pgvector_rejects_means_no_hits_not_an_error()
    {
        await using var db = postgres.CreateDbContext();
        var message = Query();
        var nan = Enumerable.Repeat(float.NaN, Dimension).ToArray();
        var vectors = new MessageVectors(EmbeddingModel, new Dictionary<string, Vector> { [message.Id] = new(nan) });

        (await Memory(db, new FakeEmbeddingGenerator(Dimension)).FindSimilarAsync([message], vectors, 5, null, [], Ct)).ShouldBeEmpty();
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
                    var row = Decision($"sender{n % 500}@example.com", $"Label {n % 20}");
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

    private DecisionMemory Memory(AppDbContext db, FakeEmbeddingGenerator generator) => MemoryTestFactory.Memory(db, settings, generator);

    private DecisionEmbeddingService EmbeddingService(FakeEmbeddingGenerator generator) =>
        MemoryTestFactory.EmbeddingService(postgres, db => Memory(db, generator), providers);

    private static MessageRow Query() => new()
    {
        Id = "q1", ThreadId = "t-q1", FromAddress = "query@example.com", Subject = "Synthetic", InternalDate = Now, FetchedAt = Now, UpdatedAt = Now,
    };

    /// <summary>A unit vector close to <paramref name="centre"/>'s, so clusters fall within the memory's distance limit.</summary>
    private static float[] Near(FakeEmbeddingGenerator generator, string centre, string noise, float spread = 0.3f)
    {
        var c = generator.Vector(centre);
        var e = generator.Vector(noise);
        var v = c.Select((x, i) => x + (spread * e[i])).ToArray();
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return [.. v.Select(x => x / norm)];
    }

    private static async Task<string> PlanAsync(AppDbContext db, Vector query)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("EXPLAIN " + string.Format(DecisionMemory.NearestSql(Dimension, excluding: false), "$1", "$2", "$3"), connection);
        command.Parameters.Add(new NpgsqlParameter { Value = EmbeddingModel });
        command.Parameters.Add(new NpgsqlParameter { Value = query });
        command.Parameters.Add(new NpgsqlParameter { Value = 5 });
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        await connection.CloseAsync();
        return string.Join('\n', lines);
    }

    private static async Task<List<(string, bool)>> IndexesAsync(AppDbContext db) =>
        [.. (await db.Database.SqlQuery<IndexRow>($"""
            SELECT c.relname AS "Name", i.indisvalid AS "Valid" FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relname LIKE 'ix_decisions_embedding_hnsw_%' ORDER BY c.relname
            """).ToListAsync(Ct)).Select(r => (r.Name, r.Valid))];

    private async Task ResetAsync()
    {
        await using var db = postgres.CreateDbContext();
        foreach (var (name, _) in await IndexesAsync(db))
        {
            var drop = "DROP INDEX IF EXISTS \"" + name + "\"";
            await db.Database.ExecuteSqlRawAsync(drop, Ct);
        }

        await db.Decisions.ExecuteDeleteAsync(Ct);
    }

    private static DecisionRow Decision(string sender, string label) => new()
    {
        Id = Guid.NewGuid(),
        SenderAddress = sender,
        SubjectTemplate = "synthetic #",
        TopicLabel = label,
        Outcome = DecisionOutcome.Approved,
        Source = SuggestionSource.Llm,
        CreatedAt = Now,
    };

    private sealed record IndexRow(string Name, bool Valid);
}
