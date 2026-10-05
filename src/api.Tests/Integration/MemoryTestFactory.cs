using GmailOrganiser.Data;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Memory;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Builds decision memory, the index maintainer and the background embedding pass over the test database.</summary>
internal static class MemoryTestFactory
{
    public static DecisionMemory Memory(AppDbContext db, ISettingsStore settings, FakeEmbeddingGenerator embeddings) =>
        new(db, new FakeLlmClientFactory(embed: embeddings), settings, NullLogger<DecisionMemory>.Instance);

    public static EmbeddingIndexMaintainer Indexes(AppDbContext db, TimeProvider? clock = null) =>
        new(db, clock ?? TimeProvider.System, NullLogger<EmbeddingIndexMaintainer>.Instance);

    /// <summary>A pass with its own scopes; <paramref name="providers"/> collects the provider for the test to dispose.</summary>
    public static DecisionEmbeddingService EmbeddingService(
        PostgresFixture postgres, Func<AppDbContext, IDecisionMemory> memory, ICollection<ServiceProvider> providers, TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => postgres.CreateDbContext());
        services.AddScoped(sp => memory(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped(sp => Indexes(sp.GetRequiredService<AppDbContext>(), clock));
        var provider = services.BuildServiceProvider();
        providers.Add(provider);
        return new DecisionEmbeddingService(
            provider.GetRequiredService<IServiceScopeFactory>(), new DecisionEmbeddingQueue(), clock ?? TimeProvider.System,
            NullLogger<DecisionEmbeddingService>.Instance);
    }
}
