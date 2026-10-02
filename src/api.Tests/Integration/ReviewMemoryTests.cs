using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Review decisions and decision memory (#111): embedding stays off the request path, post-commit work is best effort.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReviewMemoryTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string EmbeddingModel = "fake-embedding-model";
    private readonly RecordingEmbeddingQueue queue = new();
    private readonly FakeEmbeddingGenerator embeddings = new();
    private readonly PostgresFixture postgres;
    private readonly AnalysisRunHarness h;

    public ReviewMemoryTests(ApiFactory factory, PostgresFixture postgres)
    {
        this.postgres = postgres;
        h = new(factory, postgres)
        {
            Embeddings = embeddings,
            ConfigureServices = services => services.AddSingleton<IDecisionEmbeddingQueue>(queue),
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(s => s with { EmbeddingModel = EmbeddingModel }, Ct);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Approve_returns_without_calling_the_embedder_and_a_later_pass_embeds_the_decision()
    {
        Guid id;
        await using (var db = postgres.CreateDbContext())
        {
            id = await db.Suggestions.Where(s => s.MessageId == "a00").Select(s => s.Id).SingleAsync(Ct);
        }

        (await h.PostAsync($"/api/review/suggestions/{id}/approve", new { })).StatusCode.ShouldBe(HttpStatusCode.OK);

        embeddings.Inputs.ShouldBeEmpty();
        queue.Notified.ShouldBe(1);
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Decisions.AsNoTracking().SingleAsync(Ct)).Embedding.ShouldBeNull();
        }

        (await ActivatorUtilities.CreateInstance<DecisionEmbeddingService>(h.Services).EmbedPendingAsync(Ct)).ShouldBe(1);

        embeddings.Inputs.Count.ShouldBe(1);
        await using var check = postgres.CreateDbContext();
        (await check.Decisions.AsNoTracking().SingleAsync(Ct)).EmbeddingModel.ShouldBe(EmbeddingModel);
    }

    [Fact]
    public async Task A_failing_post_commit_step_neither_fails_nor_stops_a_bulk_approve()
    {
        queue.Failure = new InvalidOperationException("synthetic post-commit failure");

        var response = await h.PostAsync("/api/review/bulk-approve", new BulkApproveRequest(0, IncludeDerived: true));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<BulkApproveResponse>(Ct)).ShouldNotBeNull();
        await using var db = postgres.CreateDbContext();
        var approved = await db.Suggestions.CountAsync(s => s.Status == SuggestionStatus.Approved, Ct);
        (result.Approved, approved).ShouldBe((20, 20));
        (await db.Decisions.CountAsync(Ct)).ShouldBe(20);
        queue.Notified.ShouldBeGreaterThan(0);
    }
}

/// <summary>Counts wake-ups of the decision embedding; throws <see cref="Failure"/> when set.</summary>
internal sealed class RecordingEmbeddingQueue : IDecisionEmbeddingQueue
{
    private int notified;

    public int Notified => Volatile.Read(ref notified);
    public Exception? Failure { get; set; }

    public void Notify()
    {
        Interlocked.Increment(ref notified);
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}
