using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class EmbeddingGroupRefinerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Shop = "shop@example.com";
    private const double Distance = 0.2;
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly GroupingSettings Grouping = GroupingSettings.From(new AppSettings()) with
    {
        Mode = AnalysisGroupingMode.Auto,
        MinGroupSize = 2,
        ClusterDistance = Distance,
    };

    // Vectors by snippet: the embedded text ends with the snippet.
    private readonly Dictionary<string, float[]> vectors = new(StringComparer.Ordinal);
    private readonly FakeEmbeddingGenerator embeddings = new(4);
    private readonly InMemorySettingsStore settings = new(new AppSettings
    {
        OllamaBaseUrl = "http://ollama.example.com:11434",
        EmbeddingModel = "synthetic-embed-a",
    });

    private ServiceProvider? provider;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        embeddings.VectorFor = text => vectors.FirstOrDefault(p => text.EndsWith("| " + p.Key, StringComparison.Ordinal)).Value;
        await using var db = postgres.CreateDbContext();
        await db.MessageEmbeddings.ExecuteDeleteAsync(Ct);
        await db.Suggestions.ExecuteDeleteAsync(Ct);
        await db.Messages.ExecuteDeleteAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (provider is not null)
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task Two_deterministic_groups_with_near_identical_vectors_merge_into_one_cluster()
    {
        var messages = await SeedAsync(
            Msg(1, "Weekly deals 12", Angle(0.00)), Msg(2, "Weekly deals 13", Angle(0.02)), Msg(3, "Weekly deals 14", Angle(0.04)),
            Msg(4, "Summer news", Angle(0.01)), Msg(5, "Summer news", Angle(0.03)));

        var result = await GroupAsync(messages);

        result.EmbeddingFallback.ShouldBeFalse();
        var group = result.Groups.ShouldHaveSingleItem();
        group.Key.ShouldBe($"emb:{Shop}:1");
        group.Members.Select(m => m.Id).ShouldBe(["m5", "m4", "m3", "m2", "m1"]);
        group.Individual.ShouldBeFalse();
        group.RepresentativeIds.Count.ShouldBe(Grouping.RepresentativesPerGroup);
        group.RepresentativeIds.ShouldContain("m5"); // newest
        group.Display.ShouldBe("Weekly deals 13"); // m2, nearest the centroid (mean angle 0.02)
    }

    [Fact]
    public async Task A_far_vector_splits_out_as_a_singleton()
    {
        var messages = await SeedAsync(
            Msg(1, "Weekly deals", Angle(0.00)), Msg(2, "Weekly deals", Angle(0.02)), Msg(3, "Weekly deals", Far()),
            Msg(4, "Weekly deals", Angle(0.01)));

        var groups = (await GroupAsync(messages)).Groups;

        groups.Count.ShouldBe(2);
        groups.Single(g => !g.Individual).Members.Select(m => m.Id).ShouldBe(["m4", "m2", "m1"]);
        var single = groups.Single(g => g.Individual);
        (single.Key, single.RepresentativeIds.Single()).ShouldBe((AnalysisGrouper.IndividualKeyPrefix + "m3", "m3"));
    }

    [Theory]
    [InlineData(Distance - 0.005, 1)]
    [InlineData(Distance + 0.005, 0)]
    public async Task Threshold_edge_merges_within_the_distance_and_splits_beyond_it(double distance, int clusters)
    {
        // Cosine distance between angle 0 and angle t is 1 - cos t.
        var angle = Math.Acos(1 - distance);
        var messages = await SeedAsync(Msg(1, "Offer one", Angle(0)), Msg(2, "Offer two", Angle(angle)));

        var groups = (await GroupAsync(messages)).Groups;

        groups.Count(g => !g.Individual).ShouldBe(clusters);
        groups.Sum(g => g.Members.Count).ShouldBe(2);
    }

    [Fact]
    public async Task Without_a_model_the_groups_stay_deterministic_and_the_run_is_told()
    {
        settings.Current = settings.Current with { EmbeddingModel = null };
        var messages = await SeedAsync(Msg(1, "Weekly deals", Angle(0)), Msg(2, "Weekly deals", Angle(0.01)));

        var result = await GroupAsync(messages);

        result.EmbeddingFallback.ShouldBeTrue();
        result.Groups.ShouldHaveSingleItem().Key.ShouldStartWith(GroupKey.FromPrefix);
        embeddings.Inputs.ShouldBeEmpty();
    }

    public static TheoryData<string> Failures => new() { "http", "dimension" };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task An_embedding_failure_falls_back_without_storing_anything(string failure)
    {
        var messages = await SeedAsync(Msg(1, "Weekly deals", Angle(0)), Msg(2, "Weekly deals", [1f, 0f]));
        if (failure == "http")
        {
            embeddings.Failure = new HttpRequestException("synthetic outage");
        }

        var result = await GroupAsync(messages);

        result.EmbeddingFallback.ShouldBeTrue();
        result.Groups.ShouldHaveSingleItem().Key.ShouldStartWith(GroupKey.FromPrefix);
        await using var db = postgres.CreateDbContext();
        (await db.MessageEmbeddings.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Stored_rows_are_reused_and_a_model_change_re_embeds()
    {
        var messages = await SeedAsync(Msg(1, "Weekly deals", Angle(0)), Msg(2, "Weekly deals", Angle(0.01)), Msg(3, "Other", Far()));

        await GroupAsync(messages);
        embeddings.Inputs.Count.ShouldBe(3);
        embeddings.Inputs.ShouldContain(EmbeddingGroupRefiner.Text(messages[0]));

        await GroupAsync(messages);
        embeddings.Inputs.Count.ShouldBe(3);

        settings.Current = settings.Current with { EmbeddingModel = "synthetic-embed-b" };
        (await GroupAsync(messages)).EmbeddingFallback.ShouldBeFalse();
        embeddings.Inputs.Count.ShouldBe(6);

        await using var db = postgres.CreateDbContext();
        var rows = await db.MessageEmbeddings.AsNoTracking().ToListAsync(Ct);
        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(r => r.Model == "synthetic-embed-b" && r.Dimension == 4);
    }

    [Fact]
    public async Task Members_are_embedded_in_batches_and_other_modes_are_untouched()
    {
        var messages = await SeedAsync([.. Enumerable.Range(1, EmbeddingGroupRefiner.BatchSize + 3).Select(i => Msg(i, "Weekly deals", Angle(0)))]);

        (await GroupAsync(messages, Grouping with { Mode = AnalysisGroupingMode.SenderSubject })).EmbeddingFallback.ShouldBeFalse();
        embeddings.Inputs.ShouldBeEmpty();

        var groups = (await GroupAsync(messages)).Groups;
        embeddings.Inputs.Count.ShouldBe(messages.Count);
        groups.ShouldHaveSingleItem().Members.Count.ShouldBe(messages.Count);
    }

    private async Task<GroupingResult> GroupAsync(IReadOnlyList<MessageRow> messages, GroupingSettings? grouping = null)
    {
        provider ??= Services();
        var refiner = new EmbeddingGroupRefiner(
            settings,
            new FakeLlmClientFactory(embed: embeddings),
            new MessageEmbeddingStore(provider.GetRequiredService<IServiceScopeFactory>(), new FakeTimeProvider(Now)),
            NullLogger<EmbeddingGroupRefiner>.Instance);
        return await new AnalysisGrouper(refiner).GroupAsync(messages, grouping ?? Grouping, Allowlist.Empty, PersonalLabels.None, Ct);
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => postgres.CreateDbContext());
        return services.BuildServiceProvider();
    }

    private async Task<IReadOnlyList<MessageRow>> SeedAsync(params (MessageRow Message, float[] Vector)[] seeded)
    {
        await using var db = postgres.CreateDbContext();
        foreach (var (message, vector) in seeded)
        {
            vectors[message.Snippet!] = vector;
            db.Messages.Add(message);
        }

        await db.SaveChangesAsync(Ct);
        return [.. seeded.Select(s => s.Message)];
    }

    private static (MessageRow, float[]) Msg(int n, string subject, float[] vector) => (new MessageRow
    {
        Id = $"m{n}",
        ThreadId = $"t{n}",
        FromAddress = Shop,
        CanonicalAddress = Shop,
        CanonicalDomain = "example.com",
        Subject = subject,
        Snippet = $"synthetic snippet {n}",
        InternalDate = Now.AddDays(n - 40),
        Category = MessageCategory.Promotions,
        LabelIds = ["INBOX"],
        FetchedAt = Now,
        UpdatedAt = Now,
    }, vector);

    private static float[] Angle(double t) => [(float)Math.Cos(t), (float)Math.Sin(t), 0f, 0f];

    private static float[] Far() => [0f, 0f, 1f, 0f];
}
