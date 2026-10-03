using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Llm;
using GmailOrganiser.Memory;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class AnalysisRunJobTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => h.InitializeAsync();

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Twenty_candidates_in_three_groups_take_three_llm_calls_and_derive_the_rest()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        run.Status.ShouldBe("queued");

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        done.Status.ShouldBe("completed");
        (done.LlmCalls, done.Groups, done.MixedGroups, done.FailedMessages).ShouldBe((3, 3, 0, 0));
        (done.MessagesCovered, done.MessagesLlm, done.MessagesDerived).ShouldBe((20, 9, 11));
        done.SavedPercent.ShouldBe(1 - (3 / 20.0), 1e-9);
        done.Model.ShouldBe(AnalysisRunHarness.ChatModel);
        done.PromptVersion.ShouldBe("analysis-v2");
        h.Chat.Calls.ShouldBe(3);

        await using var db = postgres.CreateDbContext();
        var rows = await db.Suggestions.AsNoTracking().ToListAsync(Ct);
        rows.Count.ShouldBe(20);
        rows.ShouldAllBe(s => s.RunId == run.Id && s.Status == SuggestionStatus.Pending && s.Model == AnalysisRunHarness.ChatModel);
        var derived = rows.Where(s => s.Source == SuggestionSource.Derived).ToList();
        derived.Count.ShouldBe(11);
        derived.ShouldAllBe(s => s.Reason == "Same as 3 analysed emails of this group" && s.GroupKey != null);
        derived.ShouldAllBe(s => Math.Abs(s.Confidence - 0.8) < 1e-9);
        rows.Where(s => s.MessageId.StartsWith('a')).ShouldAllBe(s => s.TopicLabel == "Shopping");

        // Bodies reach the prompt but are never stored.
        h.Chat.Requests.ShouldContain(r => r.Any(m => m.Text.Contains(AnalysisRunHarness.BodyMarker)));
        rows.ShouldAllBe(s => !s.Reason.Contains(AnalysisRunHarness.BodyMarker));

        var statuses = await db.Messages.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.AnalysisStatus, Ct);
        statuses.Where(p => !p.Key.StartsWith('x')).ShouldAllBe(p => p.Value == AnalysisStatus.Analysed);
        statuses.Where(p => p.Key.StartsWith('x')).ShouldAllBe(p => p.Value == AnalysisStatus.NotAnalysed);
        (await db.Senders.AsNoTracking().OrderBy(s => s.Address).Select(s => s.AnalysedCount).ToListAsync(Ct))
            .ShouldBe([4, 6, 0, 10]); // billing, news, other, shop

        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == run.JobId, Ct);
        (job.Status, job.Queue, job.DedupKey).ShouldBe((JobStatus.Completed, JobQueues.Analysis, run.Id.ToString()));
        JsonSerializer.Deserialize<AnalysisRunCursor>(job.Cursor!, JsonSerializerOptions.Web)!.GroupsDone.ShouldBe(3);
        h.Progress(job.Id)[^1].ShouldBe(new JobProgress(20, 20, "3 groups, 3 LLM calls"));
    }

    [Fact]
    public async Task Restart_mid_run_repeats_at_most_one_group()
    {
        using var stop = new CancellationTokenSource();
        h.Chat.Respond = (ids, call, _, ct) =>
        {
            if (call == 2)
            {
                // The host stops while the second group waits for the model: its answer is lost.
                stop.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return Task.FromResult(AnalysisRunHarness.Agree(ids));
        };
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync(stop.Token);
        (await h.GetRunAsync(run.Id)).MessagesCovered.ShouldBe(10);
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        done.Status.ShouldBe("completed");
        (done.MessagesCovered, done.MessagesDerived, done.LlmCalls).ShouldBe((20, 11, 3));
        h.Chat.Calls.ShouldBe(4);
        await using var db = postgres.CreateDbContext();
        (await db.Suggestions.CountAsync(Ct)).ShouldBe(20);
    }

    [Fact]
    public async Task Cancel_stops_after_the_current_group_and_keeps_its_suggestions()
    {
        AnalysisRunDto? run = null;
        h.Chat.Respond = async (ids, call, _, _) =>
        {
            if (call == 1)
            {
                var response = await h.PostAsync($"/api/analysis/runs/{run!.Id}/cancel", new { });
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            return AnalysisRunHarness.Agree(ids);
        };
        run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.LlmCalls).ShouldBe(("cancelled", 10, 1));
        done.FinishedAt.ShouldNotBeNull();
        await using var db = postgres.CreateDbContext();
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == run.JobId, Ct)).Status.ShouldBe(JobStatus.Cancelled);
        (await db.Suggestions.CountAsync(Ct)).ShouldBe(10);
    }

    [Fact]
    public async Task Mixed_group_sends_its_other_members_to_the_model_one_by_one()
    {
        // The shop representatives disagree; every single-email prompt answers like the first representative.
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Answer(ids, (id, i) =>
            !id.StartsWith('a') ? AnalysisRunHarness.LabelFor(id) : i == 0 ? "Shopping" : "Deals"));
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        done.Status.ShouldBe("completed");
        (done.LlmCalls, done.MixedGroups, done.Groups).ShouldBe((10, 1, 10));
        (done.MessagesCovered, done.MessagesLlm, done.MessagesDerived).ShouldBe((20, 16, 4));
        await using var db = postgres.CreateDbContext();
        (await db.Suggestions.Where(s => s.MessageId.StartsWith("a")).ToListAsync(Ct))
            .ShouldAllBe(s => s.Source == SuggestionSource.Llm);
    }

    [Fact]
    public async Task Group_with_an_invalid_representative_is_mixed_and_derives_nothing()
    {
        // The model never answers the last shop representative, not even on the retry.
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(ids.Count > 1 && ids[0].StartsWith('a')
            ? AnalysisRunHarness.Agree([.. ids.Take(ids.Count - 1)])
            : AnalysisRunHarness.Agree(ids));
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        done.Status.ShouldBe("completed");
        (done.LlmCalls, done.MixedGroups, done.FailedMessages).ShouldBe((11, 1, 1));
        (done.MessagesCovered, done.MessagesLlm, done.MessagesDerived).ShouldBe((19, 15, 4));
        await using var db = postgres.CreateDbContext();
        (await db.Suggestions.Where(s => s.MessageId.StartsWith("a")).ToListAsync(Ct))
            .ShouldAllBe(s => s.Source == SuggestionSource.Llm);
    }

    [Fact]
    public async Task Invalid_output_is_retried_once_then_recorded_as_failed_and_the_run_continues()
    {
        h.Chat.Respond = (ids, _, _, _) =>
            Task.FromResult(ids.Any(id => id.StartsWith('c')) ? "Sorry, I cannot help with that." : AnalysisRunHarness.Agree(ids));
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        done.Status.ShouldBe("completed");
        (done.MessagesCovered, done.FailedMessages, done.LlmCalls).ShouldBe((16, 4, 4));
        h.Chat.Requests[^1][^1].Text.ShouldBe(AnalysisRunJob.RetryInstruction);
        await using var db = postgres.CreateDbContext();
        (await db.Messages.Where(m => m.Id.StartsWith("c")).Select(m => m.AnalysisStatus).ToListAsync(Ct))
            .ShouldAllBe(s => s == AnalysisStatus.NotAnalysed);
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == run.JobId, Ct);
        JsonSerializer.Deserialize<AnalysisRunCursor>(job.Cursor!, JsonSerializerOptions.Web)!.FailedIds!.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Unreachable_model_fails_the_run_and_keeps_finished_groups()
    {
        h.Chat.Respond = (ids, _, _, _) => ids.Any(id => id.StartsWith('b'))
            ? throw new HttpRequestException("Connection refused")
            : Task.FromResult(AnalysisRunHarness.Agree(ids));
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered).ShouldBe(("failed", 10));
        done.Error.ShouldNotBeNullOrWhiteSpace();
        await using var db = postgres.CreateDbContext();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == run.JobId, Ct);
        (job.Status, job.Error).ShouldBe((JobStatus.Failed, done.Error));
        (await db.Suggestions.CountAsync(Ct)).ShouldBe(10);
    }
}

/// <summary>
/// A host with the fake mailbox, a scripted chat model and no background runner, over 20 inbox messages in three
/// groups (shop 10, news 6, billing 4; newest group first) and five older singles.
/// </summary>
internal sealed class AnalysisRunHarness(ApiFactory factory, PostgresFixture postgres)
{
    public const string ChatModel = "fake-chat-model";
    public const string BodyMarker = "SYNTHETIC-BODY-TEXT";
    public const string Shop = "shop@example.com";
    public const string News = "news@example.com";
    public const string Billing = "billing@example.com";
    public const string Other = "other@example.com";
    private static readonly DateTimeOffset Newest = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ConcurrentQueue<JobDto> published = new();
    private WebApplicationFactory<Program> host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ScriptedChatClient Chat { get; } = new();

    /// <summary>The embedding generator the host hands out; none (a failing factory) by default.</summary>
    public FakeEmbeddingGenerator? Embeddings { get; init; }

    /// <summary>Changes seeded messages, for example to add attachments; applied to the mailbox and the stored rows.</summary>
    public Func<FakeMessage, FakeMessage>? Customise { get; init; }

    /// <summary>Extra test services, applied last.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }
    public JobRunner Runner { get; private set; } = null!;
    public CountingGmailClient Gmail { get; private set; } = null!;

    public IServiceProvider Services => host.Services;

    /// <summary>Runs on every published job change, before it is recorded.</summary>
    public Func<JobDto, Task>? OnPublish { get; set; }

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.ExecuteDeleteAsync();
            await db.Decisions.ExecuteDeleteAsync();
            await db.AnalysisRuns.ExecuteDeleteAsync();
            await db.Jobs.ExecuteDeleteAsync();
            await db.Messages.ExecuteDeleteAsync();
            await db.Senders.ExecuteDeleteAsync();
            await db.Settings.ExecuteDeleteAsync();
            await db.FetchState.ExecuteUpdateAsync(s => s.SetProperty(r => r.AccountEmail, (string?)null));
            var seed = Seed();
            db.Messages.AddRange(seed.Select(m => new MessageRow
            {
                Id = m.Id,
                ThreadId = m.ThreadId,
                FromAddress = m.From,
                Subject = m.Subject,
                InternalDate = m.Date,
                LabelIds = [.. m.LabelIds],
                Category = MessageCategory.Updates,
                HasAttachment = m.HasAttachment,
                FetchedAt = Newest,
                UpdatedAt = Newest,
            }));
            db.Senders.AddRange(seed.Select(m => m.From).Distinct().Select(a => new SenderRow
            {
                Address = a,
                Domain = "example.com",
                TotalCount = seed.Count(m => m.From == a),
                UpdatedAt = Newest,
            }));
            await db.SaveChangesAsync();
        }

        host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true").ConfigureTestServices(services =>
        {
            services.AddSingleton(sp => new FakeGmailClient(sp.GetRequiredService<FakeTokenStore>(), Seed()));
            services.AddSingleton(sp => new CountingGmailClient(sp.GetRequiredService<FakeGmailClient>()));
            services.AddScoped<IGmailClient>(sp => sp.GetRequiredService<CountingGmailClient>());
            services.AddScoped<ILlmClientFactory>(_ => new ScriptedLlmFactory(Chat, Embeddings));
            services.AddSingleton<IJobProgressPublisher>(new RecordingPublisher(this));
            // Tests drive the job runner and the decision embedding themselves.
            services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner)));
            services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(DecisionEmbeddingService)));
            ConfigureServices?.Invoke(services);
        }));
        Runner = ActivatorUtilities.CreateInstance<JobRunner>(host.Services);
        Gmail = host.Services.GetRequiredService<CountingGmailClient>();
        await SetChatModelAsync(ChatModel);
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync();
    }

    public async Task SetChatModelAsync(string? model)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(x => x with { ChatModel = model }, Ct);
    }

    public Task<HttpResponseMessage> PostAsync(string path, object body)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client.PostAsJsonAsync(path, body, Ct);
    }

    public Task<HttpResponseMessage> PostWithoutBodyAsync(string path)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client.PostAsync(path, null, Ct);
    }

    /// <summary>Returns once some session waits for a row lock or a uniqueness check another transaction holds.</summary>
    public async Task WaitForLockWaitAsync()
    {
        await using var db = postgres.CreateDbContext();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while ((await db.Database
                   .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock'")
                   .ToListAsync(timeout.Token))[0] == 0)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    public Task<HttpResponseMessage> PutAsync(string path, object body)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client.PutAsJsonAsync(path, body, Ct);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => host.CreateClient().GetAsync(path, Ct);

    public async Task<AnalysisRunDto> StartAsync(StartAnalysisRunRequest request)
    {
        var response = await PostAsync("/api/analysis/runs", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<AnalysisRunDto>(Ct)).ShouldNotBeNull();
    }

    public async Task<AnalysisRunDto> GetRunAsync(Guid id) =>
        (await host.CreateClient().GetFromJsonAsync<AnalysisRunDto>($"/api/analysis/runs/{id}", Ct)).ShouldNotBeNull();

    public async Task RunNextAsync(CancellationToken? ct = null)
    {
        var id = (await Runner.ClaimAsync(Ct)).ShouldHaveSingleItem();
        await Runner.RunAsync(id, ct ?? Ct);
    }

    public List<JobProgress> Progress(Guid jobId) =>
        [.. published.Where(j => j.Id == jobId && j.Progress is not null).Select(j => j.Progress!)];

    public static async Task DecideAsync(
        Data.AppDbContext db, string messageId, SuggestionStatus status, Action<SuggestionRow>? change = null)
    {
        var suggestion = await db.Suggestions.SingleAsync(s => s.MessageId == messageId, Ct);
        var message = await db.Messages.SingleAsync(m => m.Id == messageId, Ct);
        change?.Invoke(suggestion);
        suggestion.SetStatus(status, message, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(Ct);
    }

    public static string LabelFor(string id) => id[0] switch
    {
        'a' => "Shopping",
        'b' => "News",
        'c' => "Finance",
        _ => "Other",
    };

    /// <summary>Every email gets its sender's label with confidence 0.9.</summary>
    public static string Agree(IReadOnlyList<string> ids) => Answer(ids, (id, _) => LabelFor(id));

    public static string Answer(IReadOnlyList<string> ids, Func<string, int, string> label) => JsonSerializer.Serialize(new
    {
        suggestions = ids.Select((id, i) => new
        {
            id,
            topicLabel = label(id, i),
            isNewLabel = false,
            needsAction = false,
            toBeDeleted = false,
            unsubscribeSuggested = false,
            confidence = 0.9,
            reason = "Synthetic reason",
        }),
    });

    private List<FakeMessage> Seed() => [.. SeedMessages().Select(m => Customise?.Invoke(m) ?? m)];

    private static List<FakeMessage> SeedMessages() =>
    [
        .. Enumerable.Range(0, 10).Select(i => Message($"a{i:D2}", Shop, $"Weekly offer {i + 1}", i)),
        .. Enumerable.Range(0, 6).Select(i => Message($"b{i:D2}", News, $"Newsletter issue {i + 1}", 20 + i)),
        .. Enumerable.Range(0, 4).Select(i => Message($"c{i:D2}", Billing, $"Invoice {i + 1}", 40 + i)),
        .. Enumerable.Range(0, 5).Select(i => Message($"x{i:D2}", Other, $"Topic {(char)('k' + i)}", 60 + i)),
    ];

    private static FakeMessage Message(string id, string from, string subject, int hoursAgo) =>
        new(id, $"t-{id}", from, subject, Newest.AddHours(-hoursAgo), ["INBOX", "CATEGORY_UPDATES"],
            BodyText: $"Synthetic body of {id}. {BodyMarker}");

    private sealed class RecordingPublisher(AnalysisRunHarness harness) : IJobProgressPublisher
    {
        public async Task JobChangedAsync(JobDto job, CancellationToken ct)
        {
            if (harness.OnPublish is { } onPublish)
            {
                await onPublish(job);
            }

            harness.published.Enqueue(job);
        }
    }
}

/// <summary>Answers through <see cref="Respond"/> with the email ids found in the prompt; records every request.</summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly List<IReadOnlyList<ChatMessage>> requests = [];
    private int calls;

    public Func<IReadOnlyList<string>, int, IReadOnlyList<ChatMessage>, CancellationToken, Task<string>> Respond { get; set; } =
        (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Agree(ids));

    public int Calls => Volatile.Read(ref calls);

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests
    {
        get
        {
            lock (requests)
            {
                return [.. requests];
            }
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> list = [.. messages];
        lock (requests)
        {
            requests.Add(list);
        }

        var call = Interlocked.Increment(ref calls);
        var ids = list.Where(m => m.Role == ChatRole.User)
            .SelectMany(m => m.Text.Split('\n'))
            .Where(l => l.StartsWith("id: ", StringComparison.Ordinal))
            .Select(l => l[4..].Trim())
            .Distinct()
            .ToList();
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, await Respond(ids, call, list, cancellationToken)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class ScriptedLlmFactory(IChatClient chat, IEmbeddingGenerator<string, Embedding<float>>? embed = null) : ILlmClientFactory
{
    public Task<IChatClient> CreateChatClientAsync(CancellationToken ct = default) => Task.FromResult(chat);

    public Task<IEmbeddingGenerator<string, Embedding<float>>> CreateEmbeddingGeneratorAsync(CancellationToken ct = default) =>
        Task.FromResult(embed ?? throw new NotSupportedException());

    public IChatClient CreateChatClient(Uri baseUrl, string model) => chat;

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(Uri baseUrl, string model) =>
        embed ?? throw new NotSupportedException();
}
