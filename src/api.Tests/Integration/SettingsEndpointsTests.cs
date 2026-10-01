using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Common;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class SettingsEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string ClientId = "test-client.apps.googleusercontent.com";
    private const string ClientSecret = "synthetic-secret-value-123";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Defaults_when_no_row_exists()
    {
        var settings = await GetAsync(factory);

        settings.OllamaBaseUrl.ShouldBe(ApiFactory.EnvOllamaBaseUrl);
        settings.ChatModel.ShouldBeNull();
        settings.EmbeddingModel.ShouldBeNull();
        settings.ActionLabelName.ShouldBe("Action/ToDo");
        settings.DeleteLabelName.ShouldBe("To-Be-Deleted");
        settings.SetupWizardSeen.ShouldBeFalse();
        settings.GoogleClient.ShouldBe(new GoogleClientDto(null, SecretSet: false, LockedByEnv: false));
    }

    [Fact]
    public async Task Code_default_url_applies_without_env()
    {
        await using var noEnv = factory.WithWebHostBuilder(b => b.UseSetting("OLLAMA_BASE_URL", ""));

        (await GetAsync(noEnv)).OllamaBaseUrl.ShouldBe(AppSettings.FallbackOllamaBaseUrl);
    }

    [Fact]
    public async Task Update_round_trips_and_is_partial()
    {
        await PutAsync(factory, "/api/settings", new UpdateSettingsRequest("https://llm.example.com/", "chat-model-a", "embed-model-a", true));
        var response = await PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, "chat-model-b", "", null));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var settings = await GetAsync(factory);
        settings.OllamaBaseUrl.ShouldBe("https://llm.example.com/");
        settings.ChatModel.ShouldBe("chat-model-b");
        settings.EmbeddingModel.ShouldBeNull();
        settings.SetupWizardSeen.ShouldBeTrue();
    }

    [Fact]
    public async Task Saved_value_wins_over_env_and_unsaved_values_follow_env()
    {
        await PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, "chat-model-a", null, null));
        await using var otherEnv = factory.WithWebHostBuilder(b => b.UseSetting("OLLAMA_BASE_URL", "http://other.example.com:11434"));

        (await GetAsync(otherEnv)).OllamaBaseUrl.ShouldBe("http://other.example.com:11434");

        await PutAsync(factory, "/api/settings", new UpdateSettingsRequest("http://saved.example.com:11434", null, null, null));
        (await GetAsync(otherEnv)).OllamaBaseUrl.ShouldBe("http://saved.example.com:11434");
    }

    [Fact]
    public async Task Unknown_or_unreadable_saved_values_fall_back_to_defaults()
    {
        await SeedDocumentAsync("""{"unknownSetting":1,"setupWizardSeen":"yes","ollamaBaseUrl":null,"chatModel":"chat-model-a"}""");

        var settings = await GetAsync(factory);

        settings.SetupWizardSeen.ShouldBeFalse();
        settings.OllamaBaseUrl.ShouldBe(ApiFactory.EnvOllamaBaseUrl);
        settings.ChatModel.ShouldBe("chat-model-a");
    }

    [Fact]
    public async Task Google_client_secret_is_stored_encrypted_and_never_returned()
    {
        var response = await PutAsync(factory, "/api/settings/google-client", new GoogleClientRequest(ClientId, ClientSecret));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain(ClientSecret);
        var getBody = await factory.CreateClient().GetStringAsync("/api/settings", Ct);
        getBody.ShouldNotContain(ClientSecret);
        (await GetAsync(factory)).GoogleClient.ShouldBe(new GoogleClientDto(ClientId, SecretSet: true, LockedByEnv: false));

        await using var db = postgres.CreateDbContext();
        var document = (await db.Settings.SingleAsync(Ct)).Document;
        document.ShouldContain(ClientId);
        document.ShouldNotContain(ClientSecret);
        document.ShouldContain("googleClientSecretProtected");
    }

    [Fact]
    public async Task Undecryptable_secret_counts_as_not_set()
    {
        await SeedDocumentAsync($$"""{"googleClientId":"{{ClientId}}","googleClientSecretProtected":"not-a-valid-ciphertext"}""");

        (await GetAsync(factory)).GoogleClient.ShouldBe(new GoogleClientDto(ClientId, SecretSet: false, LockedByEnv: false));
    }

    [Fact]
    public async Task Env_locked_google_client_returns_409_and_env_values()
    {
        await using var locked = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("GOOGLE_CLIENT_ID", "env-client.apps.googleusercontent.com");
            b.UseSetting("GOOGLE_CLIENT_SECRET", "env-secret");
        });

        var response = await PutAsync(locked, "/api/settings/google-client", new GoogleClientRequest(ClientId, ClientSecret));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await GetAsync(locked)).GoogleClient.ShouldBe(new GoogleClientDto("env-client.apps.googleusercontent.com", SecretSet: true, LockedByEnv: true));
        await using var db = postgres.CreateDbContext();
        (await db.Settings.AnyAsync(Ct)).ShouldBeFalse();
    }

    public static TheoryData<string?, string?, string?> InvalidSettings => new()
    {
        { "ftp://llm.example.com", null, null },
        { "/relative", null, null },
        { "", null, null },
        { null, new string('m', SettingsValidation.MaxModelNameLength + 1), null },
        { null, null, "model\nname" },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task Invalid_settings_get_400_problem_details(string? url, string? chatModel, string? embeddingModel)
    {
        var response = await PutAsync(factory, "/api/settings", new UpdateSettingsRequest(url, chatModel, embeddingModel, null));

        await ShouldBeValidationProblemAsync(response);
    }

    [Theory]
    [InlineData("not-a-client-id", ClientSecret)]
    [InlineData(".apps.googleusercontent.com", ClientSecret)]
    [InlineData("a b.apps.googleusercontent.com", ClientSecret)]
    [InlineData(ClientId, "")]
    [InlineData(ClientId, "   ")]
    [InlineData(null, null)]
    public async Task Invalid_google_client_gets_400_problem_details(string? clientId, string? clientSecret)
    {
        var response = await PutAsync(factory, "/api/settings/google-client", new GoogleClientRequest(clientId, clientSecret));

        await ShouldBeValidationProblemAsync(response);
        await using var db = postgres.CreateDbContext();
        (await db.Settings.AnyAsync(Ct)).ShouldBeFalse();
    }

    private static async Task ShouldBeValidationProblemAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    private async Task SeedDocumentAsync(string document)
    {
        await using var db = postgres.CreateDbContext();
        db.Settings.Add(new SettingsRow { Document = document });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<SettingsDto> GetAsync(WebApplicationFactory<Program> host) =>
        (await host.CreateClient().GetFromJsonAsync<SettingsDto>("/api/settings", Ct)).ShouldNotBeNull();

    private static async Task<HttpResponseMessage> PutAsync<T>(WebApplicationFactory<Program> host, string path, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(ApiRequestGuardMiddleware.RequestedWithHeader, "XMLHttpRequest");
        return await host.CreateClient().SendAsync(request, Ct);
    }
}
