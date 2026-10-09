using System.Net;
using System.Net.Sockets;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit;

/// <summary>The Claude API model list over the named client from <c>AddLlm</c> and a fake API.</summary>
public sealed class ClaudeApiCatalogTests : IDisposable
{
    // Built, not a literal: a key-shaped literal trips the secret scan.
    private static readonly string SyntheticKey = new string('k', 30) + "WXYZ";

    private readonly FakeAnthropicHandler api = new();
    private readonly InMemorySettingsStore settings = new();
    private readonly ListLogger logger = new();
    private ServiceProvider? services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => services?.Dispose();

    private async Task<ClaudeApiCatalog> CreateAsync(bool withKey = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:ClaudeApiMaxRetries"] = "0",
                ["Llm:ClaudeApiBaseUrl"] = "https://claude.example.com",
            })
            .Build();
        var collection = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLlm();
        collection.AddHttpClient(ClaudeApiHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => api);
        services = collection.BuildServiceProvider();
        var keys = TestClaudeApiKeys.For(settings);
        if (withKey)
        {
            await keys.SetAsync(SyntheticKey, Ct);
        }

        return new ClaudeApiCatalog(
            services.GetRequiredService<IHttpClientFactory>(), keys, services.GetRequiredService<IOptions<LlmOptions>>(), logger);
    }

    [Fact]
    public async Task No_key_lists_nothing_without_a_call()
    {
        var catalog = await CreateAsync(withKey: false);

        var result = await catalog.ListModelsAsync(Ct);

        result.ShouldBe(ClaudeApiModelsDto.NoKey);
        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task All_pages_are_read_with_the_key_and_sorted_newest_first()
    {
        api.Models(true, ("test-model-a", "Test Model A", "2026-01-01T00:00:00Z"), ("test-model-c", "Test Model C", null))
            .Models(false, ("test-model-b", "Test Model B", "2026-05-01T00:00:00Z"), ("test-model-0", "", "2026-01-01T00:00:00Z"));
        var catalog = await CreateAsync();

        var result = await catalog.ListModelsAsync(Ct);

        result.KeySet.ShouldBeTrue();
        result.Reachable.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.Models.Select(m => m.Id).ShouldBe(["test-model-b", "test-model-0", "test-model-a", "test-model-c"]);
        result.Models[1].DisplayName.ShouldBe("test-model-0");
        result.Models[0].CreatedAt.ShouldBe(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        api.Requests.Count.ShouldBe(2);
        api.Requests.ShouldAllBe(r => r.Host == "claude.example.com" && r.Path == "/v1/models");
        api.Requests[0].Query.ShouldBe("?limit=100");
        api.Requests[1].Query.ShouldBe("?limit=100&after_id=test-model-c");
        api.Requests[0].Headers["x-api-key"].ShouldBe(SyntheticKey);
        api.Requests[0].Headers.ShouldContainKey("anthropic-version");
    }

    [Fact]
    public async Task Paging_stops_after_the_page_cap()
    {
        api.Models(true, ("test-model-a", "Test Model A", "2026-01-01T00:00:00Z"));
        var catalog = await CreateAsync();

        var result = await catalog.ListModelsAsync(Ct);

        api.Requests.Count.ShouldBe(ClaudeApiCatalog.MaxPages);
        result.Reachable.ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "The Claude API rejected the key.")]
    [InlineData(HttpStatusCode.Forbidden, "The Claude API rejected the key.")]
    [InlineData(HttpStatusCode.InternalServerError, "The Claude API returned 500.")]
    [InlineData(HttpStatusCode.TooManyRequests, "The Claude API returned 429.")]
    public async Task Api_errors_are_readable_and_key_free(HttpStatusCode status, string error)
    {
        api.Error(status, "synthetic_error");
        var catalog = await CreateAsync();

        var result = await catalog.ListModelsAsync(Ct);

        result.KeySet.ShouldBeTrue();
        result.Reachable.ShouldBeFalse();
        result.Error.ShouldBe(error);
        result.Models.ShouldBeEmpty();
        logger.Messages.ShouldBe(["Claude API model list failed (ClaudeApiStatusException)"]);
    }

    [Fact]
    public async Task Network_failure_names_only_the_exception_type()
    {
        api.Throw(new HttpRequestException($"refused {SyntheticKey}", new SocketException((int)SocketError.ConnectionRefused)));
        var catalog = await CreateAsync();

        var result = await catalog.ListModelsAsync(Ct);

        result.KeySet.ShouldBeTrue();
        result.Reachable.ShouldBeFalse();
        result.Error.ShouldBe("Cannot reach the Claude API (HttpRequestException).");
        logger.Messages.ShouldBe(["Claude API model list failed (HttpRequestException)"]);
        logger.Messages.ShouldAllBe(m => !m.Contains(SyntheticKey));
    }

    [Fact]
    public async Task Timeout_is_unreachable()
    {
        api.Throw(new TaskCanceledException("synthetic timeout"));
        var catalog = await CreateAsync();

        var result = await catalog.ListModelsAsync(Ct);

        result.Error.ShouldBe("Cannot reach the Claude API (TaskCanceledException).");
    }

    [Fact]
    public async Task The_callers_cancel_propagates()
    {
        using var cts = new CancellationTokenSource();
        var catalog = await CreateAsync();
        await cts.CancelAsync();
        api.Throw(new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => catalog.ListModelsAsync(cts.Token));
    }

    private sealed class ListLogger : ILogger<ClaudeApiCatalog>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
