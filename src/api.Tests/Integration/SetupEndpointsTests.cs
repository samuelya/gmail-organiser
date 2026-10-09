using System.Net;
using System.Text.Json;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class SetupEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string StatusPath = "/api/setup/status";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.OAuthTokens.ExecuteDeleteAsync(Ct);
        await db.Settings.ExecuteDeleteAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Status_on_a_fresh_install_with_ollama_down_is_200_and_incomplete()
    {
        await using var host = Host(new StubOllamaHandler { Failure = new HttpRequestException("synthetic refusal") });

        var json = await GetStatusAsync(host);

        json.ShouldBe(new Dictionary<string, bool>
        {
            ["googleClientConfigured"] = false,
            ["gmailConnected"] = false,
            ["gmailReauthRequired"] = false,
            ["ollamaReachable"] = false,
            ["chatModelSelected"] = false,
            ["embeddingModelSelected"] = false,
            ["wizardSeen"] = false,
            ["complete"] = false,
            ["accountMismatch"] = false,
            ["completedOnce"] = false,
        });
    }

    [Fact]
    public async Task Status_with_fake_gmail_and_a_saved_chat_model_is_complete()
    {
        await using var host = Host(StubOllamaHandler.WithModels(("test-chat", ["completion"])), b => b.UseSetting("GMAIL_FAKE", "true"));
        using (var client = host.CreateClient())
        {
            client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
            var put = await client.PutAsync(
                "/api/settings",
                new StringContent("""{"chatModel":"test-chat","setupWizardSeen":true}""", System.Text.Encoding.UTF8, "application/json"),
                Ct);
            put.IsSuccessStatusCode.ShouldBeTrue();
        }

        var json = await GetStatusAsync(host);

        json["gmailConnected"].ShouldBeTrue();
        json["googleClientConfigured"].ShouldBeTrue();
        json["ollamaReachable"].ShouldBeTrue();
        json["chatModelSelected"].ShouldBeTrue();
        json["embeddingModelSelected"].ShouldBeFalse();
        json["wizardSeen"].ShouldBeTrue();
        json["complete"].ShouldBeTrue();
        json["completedOnce"].ShouldBeTrue();

        // The fake Gmail token says nothing about a real account, so the flag is reported but never stored.
        await using var scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<ISettingsStore>().GetAsync(Ct)).SetupCompletedOnce.ShouldBeFalse();
    }

    [Fact]
    public async Task A_token_needing_reauth_with_a_chat_model_backfills_completed_once()
    {
        await using var host = Host(StubOllamaHandler.WithModels(("test-chat", ["completion"])));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<ITokenStore>();
            await tokens.SaveAsync("owner@example.com", "synthetic-refresh-token", ["scope-a"], Ct);
            await tokens.MarkReauthRequiredAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(s => s with { ChatModel = "test-chat" }, Ct);
        }

        var json = await GetStatusAsync(host);

        json["complete"].ShouldBeFalse();
        json["completedOnce"].ShouldBeTrue();
        await using var check = host.Services.CreateAsyncScope();
        (await check.ServiceProvider.GetRequiredService<ISettingsStore>().GetAsync(Ct)).SetupCompletedOnce.ShouldBeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Claude_api_model_counts_as_selected_only_with_a_key(bool withKey)
    {
        await using var host = Host(new StubOllamaHandler { Failure = new HttpRequestException("synthetic refusal") }, b => b.UseSetting("GMAIL_FAKE", "true"));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(
                s => s with { LlmProvider = LlmProvider.ClaudeApi, ClaudeApiModel = "test-model-a", ChatModel = null }, Ct);
            var keys = scope.ServiceProvider.GetRequiredService<ClaudeApiKeyService>();
            await (withKey ? keys.SetAsync("synthetic-test-key-0000", Ct) : keys.ClearAsync(Ct));
        }

        var json = await GetStatusAsync(host);

        // Without a key every run start is refused, so setup must not read complete either.
        json["chatModelSelected"].ShouldBe(withKey);
        json["complete"].ShouldBe(withKey);
    }

    [Fact]
    public async Task A_failed_settings_update_leaves_no_tracked_settings_row()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.UpdateAsync(_ => throw new InvalidOperationException("synthetic failure"), Ct));

        scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.Entries<SettingsRow>().ShouldBeEmpty();
    }

    private static async Task<Dictionary<string, bool>> GetStatusAsync(WebApplicationFactory<Program> host)
    {
        using var client = host.CreateClient();
        var response = await client.GetAsync(StatusPath, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<Dictionary<string, bool>>(await response.Content.ReadAsStringAsync(Ct))!;
    }

    private WebApplicationFactory<Program> Host(StubOllamaHandler stub, Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(b =>
        {
            configure?.Invoke(b);
            b.ConfigureServices(services =>
                services.AddHttpClient(OllamaHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => stub));
        });
}
