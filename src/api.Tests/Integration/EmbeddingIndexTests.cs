using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Memory;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pgvector;

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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await ResetAsync();

    public async ValueTask DisposeAsync() => await ResetAsync();

    [Fact]
    public async Task Embedding_creates_the_index_for_the_model_dimension_and_a_dimension_change_swaps_it()
    {
        await using var db = postgres.CreateDbContext();
        var first = Decision("a@example.com", "Label A");
        db.Decisions.Add(first);
        await db.SaveChangesAsync(Ct);

        await Memory(db, new FakeEmbeddingGenerator(16)).EmbedAsync([first], Ct);
        await db.SaveChangesAsync(Ct);
        (await IndexesAsync(db)).ShouldBe([("ix_decisions_embedding_hnsw_16", true)]);

        // An earlier model's rows stay behind with another dimension; the partial index skips them.
        var second = Decision("b@example.com", "Label B");
        db.Decisions.Add(second);
        await db.SaveChangesAsync(Ct);
        await Memory(db, new FakeEmbeddingGenerator(24)).EmbedAsync([second], Ct);
        await db.SaveChangesAsync(Ct);
        (await IndexesAsync(db)).ShouldBe([("ix_decisions_embedding_hnsw_24", true)]);
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

        await new EmbeddingIndexMaintainer(db, NullLogger<EmbeddingIndexMaintainer>.Instance).EnsureAsync(8, Ct);

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

        var message = new MessageRow
        {
            Id = "q1", ThreadId = "t-q1", FromAddress = "query@example.com", Subject = "Synthetic", InternalDate = Now, FetchedAt = Now, UpdatedAt = Now,
        };
        var query = new Vector(Near(generator, "centre 7", "query"));
        var vectors = new MessageVectors(EmbeddingModel, new Dictionary<string, Vector> { [message.Id] = query });

        await using var scanDb = postgres.CreateDbContext();
        var scanned = await Memory(scanDb, generator).FindSimilarAsync([message], vectors, 5, null, [], Ct);
        (await PlanAsync(scanDb, query)).ShouldNotContain("ix_decisions_embedding_hnsw");

        await using var indexedDb = postgres.CreateDbContext();
        await new EmbeddingIndexMaintainer(indexedDb, NullLogger<EmbeddingIndexMaintainer>.Instance).EnsureAsync(Dimension, Ct);
        var plan = await PlanAsync(indexedDb, query);
        output.WriteLine(plan);
        plan.ShouldContain($"Index Scan using ix_decisions_embedding_hnsw_{Dimension}");
        var indexed = await Memory(indexedDb, generator).FindSimilarAsync([message], vectors, 5, null, [], Ct);

        scanned.ShouldNotBeEmpty();
        indexed.Select(h => (h.SenderAddress, h.TopicLabel, Math.Round(h.Similarity, 5)))
            .ShouldBe(scanned.Select(h => (h.SenderAddress, h.TopicLabel, Math.Round(h.Similarity, 5))));
    }

    private DecisionMemory Memory(AppDbContext db, FakeEmbeddingGenerator generator) =>
        new(db, new FakeLlmClientFactory(embed: generator), settings, new EmbeddingIndexMaintainer(db, NullLogger<EmbeddingIndexMaintainer>.Instance),
            NullLogger<DecisionMemory>.Instance);

    /// <summary>A unit vector close to <paramref name="centre"/>'s, so clusters fall within the memory's distance limit.</summary>
    private static float[] Near(FakeEmbeddingGenerator generator, string centre, string noise)
    {
        var c = generator.Vector(centre);
        var e = generator.Vector(noise);
        var v = c.Select((x, i) => x + (0.3f * e[i])).ToArray();
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
