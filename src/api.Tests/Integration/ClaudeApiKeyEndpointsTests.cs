using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GmailOrganiser.Common;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Claude API provider settings and the write-only key (#485).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ClaudeApiKeyEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    // Built, not a literal: a key-shaped literal trips the secret scan.
    private static readonly string SyntheticKey = new string('k', 30) + "WXYZ";
    private const string KeyPath = "/api/llm/claude-api/key";

    private readonly CapturingLoggerProvider logs = new();
    private WebApplicationFactory<Program> host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync();
        host = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ILoggerProvider>(logs)));
    }

    public ValueTask DisposeAsync() => host.DisposeAsync();

    [Fact]
    public async Task Defaults_when_no_row_exists()
    {
        var body = await host.CreateClient().GetStringAsync("/api/settings", Ct);

        body.ShouldContain("\"llmProvider\":\"ollama\"");
        body.ShouldContain("\"claudeApiModel\":null");
        body.ShouldContain("\"claudeApiKeySet\":false");
        body.ShouldContain("\"claudeApiKeyHint\":null");
        body.ShouldNotContain("activeChatModel", Case.Insensitive);
        body.ShouldNotContain("meterNumCtx", Case.Insensitive);
        body.ShouldNotContain("claudeApiKeyProtected", Case.Insensitive);
    }

    [Fact]
    public async Task Key_is_stored_encrypted_returned_only_as_a_hint_and_never_logged()
    {
        var put = await SendAsync(HttpMethod.Put, KeyPath, new { apiKey = $"  {SyntheticKey} " });

        put.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var body = await host.CreateClient().GetStringAsync("/api/settings", Ct);
        body.ShouldContain("\"claudeApiKeySet\":true");
        body.ShouldContain("\"claudeApiKeyHint\":\"…WXYZ\"");
        body.ShouldNotContain(SyntheticKey);
        await using (var db = postgres.CreateDbContext())
        {
            var document = (await db.Settings.SingleAsync(Ct)).Document;
            document.ShouldNotContain(SyntheticKey);
            JsonNode.Parse(document)!["claudeApiKeyProtected"].ShouldNotBeNull();
            body.ShouldNotContain(JsonNode.Parse(document)!["claudeApiKeyProtected"]!.GetValue<string>());
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ClaudeApiKeyService>().GetAsync(Ct)).ShouldBe(SyntheticKey);
        }

        logs.Entries.ShouldContain(e => e.Message == "Claude API key saved");
        logs.Entries.ShouldAllBe(e => !e.Message.Contains(SyntheticKey) && (e.Exception == null || !e.Exception.ToString().Contains(SyntheticKey)));
    }

    [Fact]
    public async Task Delete_clears_the_key_and_is_idempotent()
    {
        (await SendAsync(HttpMethod.Put, KeyPath, new { apiKey = SyntheticKey })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await SendAsync(HttpMethod.Delete, KeyPath)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Delete, KeyPath)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var settings = (await host.CreateClient().GetFromJsonAsync<SettingsDto>("/api/settings", Ct)).ShouldNotBeNull();
        settings.ClaudeApiKeySet.ShouldBeFalse();
        settings.ClaudeApiKeyHint.ShouldBeNull();
        logs.Entries.ShouldContain(e => e.Message == "Claude API key cleared");
    }

    [Theory]
    [InlineData("synthetic key with spaces 0123")]
    [InlineData("short-key-WXYZ")]
    public async Task Invalid_key_gets_400_that_does_not_echo_it(string key)
    {
        var response = await SendAsync(HttpMethod.Put, KeyPath, new { apiKey = key });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain(key);
        var problem = (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull();
        problem.Errors.Keys.ShouldBe(["apiKey"]);
        logs.Entries.ShouldAllBe(e => !e.Message.Contains(key));
        await using var db = postgres.CreateDbContext();
        (await db.Settings.AnyAsync(Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Provider_and_model_round_trip_and_an_empty_model_clears_it()
    {
        var response = await SendAsync(HttpMethod.Put, "/api/settings", JsonNode.Parse("""{"llmProvider":"claude_api","claudeApiModel":"  api-model-a "}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = (await response.Content.ReadFromJsonAsync<SettingsDto>(Ct)).ShouldNotBeNull();
        saved.LlmProvider.ShouldBe(LlmProvider.ClaudeApi);
        saved.ClaudeApiModel.ShouldBe("api-model-a");
        await using (var db = postgres.CreateDbContext())
        {
            JsonNode.Parse((await db.Settings.SingleAsync(Ct)).Document)!["llmProvider"]!.GetValue<string>().ShouldBe("claude_api");
        }

        await SendAsync(HttpMethod.Put, "/api/settings", JsonNode.Parse("""{"claudeApiModel":""}"""));

        var cleared = (await host.CreateClient().GetFromJsonAsync<SettingsDto>("/api/settings", Ct)).ShouldNotBeNull();
        cleared.ClaudeApiModel.ShouldBeNull();
        cleared.LlmProvider.ShouldBe(LlmProvider.ClaudeApi);
    }

    [Theory]
    [InlineData("""{"llmProvider":"gpt"}""")]
    [InlineData("""{"llmProvider":"Claude_Api"}""")]
    [InlineData("""{"llmProvider":5}""")]
    [InlineData("""{"llmProvider":true}""")]
    public async Task Unknown_provider_gets_a_field_error(string json)
    {
        var response = await SendAsync(HttpMethod.Put, "/api/settings", JsonNode.Parse(json));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct))!.Errors.Keys.ShouldBe(["llmProvider"]);
        (await host.CreateClient().GetFromJsonAsync<SettingsDto>("/api/settings", Ct))!.LlmProvider.ShouldBe(LlmProvider.Ollama);
    }

    [Fact]
    public async Task Undecryptable_key_is_removed_and_warned_about_once()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Settings.Add(new SettingsRow { Document = """{"claudeApiKeyProtected":"stale-ciphertext"}""" });
            await db.SaveChangesAsync(Ct);
        }

        var first = (await host.CreateClient().GetFromJsonAsync<SettingsDto>("/api/settings", Ct)).ShouldNotBeNull();
        var second = (await host.CreateClient().GetFromJsonAsync<SettingsDto>("/api/settings", Ct)).ShouldNotBeNull();

        first.ClaudeApiKeySet.ShouldBeFalse();
        second.ClaudeApiKeySet.ShouldBeFalse();
        logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("Claude API key")).ShouldBe(1);
        await using (var db = postgres.CreateDbContext())
        {
            JsonNode.Parse((await db.Settings.SingleAsync(Ct)).Document)!["claudeApiKeyProtected"].ShouldBeNull();
        }
    }

    public static TheoryData<string> InvalidModels => new()
    {
        "\"model\\nname\"",
        $"\"{new string('m', SettingsValidation.MaxModelNameLength + 1)}\"",
    };

    [Theory]
    [MemberData(nameof(InvalidModels))]
    public async Task Invalid_model_gets_a_field_error(string value)
    {
        var response = await SendAsync(HttpMethod.Put, "/api/settings", JsonNode.Parse($$"""{"claudeApiModel":{{value}}}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct))!.Errors.Keys.ShouldBe(["claudeApiModel"]);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add(ApiRequestGuardMiddleware.RequestedWithHeader, "XMLHttpRequest");
        return await host.CreateClient().SendAsync(request, Ct);
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new(logLevel, formatter(state, exception), exception));
        }
    }
}
