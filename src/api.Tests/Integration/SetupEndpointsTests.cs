using System.Net;
using System.Text.Json;
using GmailOrganiser.Llm;
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
        await using var scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<ISettingsStore>().GetAsync(Ct)).SetupCompletedOnce.ShouldBeTrue();
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
