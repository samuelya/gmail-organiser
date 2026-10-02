using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GmailOrganiser.Common;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        settings.FetchChunkSize.ShouldBe(AppSettings.DefaultFetchChunkSize);
        settings.GoogleClient.ShouldBe(new GoogleClientDto(null, SecretSet: false, LockedByEnv: false));
    }

    [Fact]
    public async Task Fetch_chunk_size_is_saved_and_kept_by_other_updates()
    {
        await PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, null, null, null, FetchChunkSize: 2500));
        await PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, "chat-model-a", null, null));

        (await GetAsync(factory)).FetchChunkSize.ShouldBe(2500);
    }

    [Theory]
    [InlineData(SettingsValidation.MinFetchChunkSize - 1)]
    [InlineData(SettingsValidation.MaxFetchChunkSize + 1)]
    [InlineData(0)]
    public async Task Fetch_chunk_size_out_of_range_gets_400_problem_details(int chunkSize)
    {
        var response = await PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, null, null, null, chunkSize));

        await ShouldBeValidationProblemAsync(response);
    }

    [Fact]
    public async Task Analysis_defaults_when_no_row_exists()
    {
        var settings = await GetAsync(factory);

        settings.AnalysisDefaultCount.ShouldBe(20);
        settings.AnalysisBodyMaxChars.ShouldBe(4000);
        settings.AnalysisGroupingMode.ShouldBe(AnalysisGroupingMode.Auto);
        settings.AnalysisRepresentativesPerGroup.ShouldBe(3);
        settings.AnalysisMinGroupSize.ShouldBe(3);
        settings.AnalysisDerivedConfidencePenalty.ShouldBe(0.10);
        settings.AnalysisClusterDistance.ShouldBe(0.15);
        settings.AnalysisMemoryShortCircuit.ShouldBeTrue();
        settings.AnalysisMemoryMinApprovals.ShouldBe(3);
        settings.BulkApproveThreshold.ShouldBe(0.80);
        settings.AutoArchiveOnActionDone.ShouldBeFalse();
        settings.AnalysisPromptTemplate.ShouldBeNull();
    }

    [Fact]
    public async Task Analysis_settings_round_trip_and_are_kept_by_other_updates()
    {
        var response = await PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, null, null, null,
            AnalysisDefaultCount: 50, AnalysisBodyMaxChars: 8000, AnalysisGroupingMode: AnalysisGroupingMode.SenderSubject,
            AnalysisRepresentativesPerGroup: 2, AnalysisMinGroupSize: 5, AnalysisDerivedConfidencePenalty: 0.2,
            AnalysisClusterDistance: 0.3, AnalysisMemoryShortCircuit: false, AnalysisMemoryMinApprovals: 7,
            BulkApproveThreshold: 0.95, AutoArchiveOnActionDone: true, AnalysisPromptTemplate: "  Classify:\n\t{{emails}}  "));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, "chat-model-a", null, null));

        var settings = await GetAsync(factory);
        settings.AnalysisDefaultCount.ShouldBe(50);
        settings.AnalysisBodyMaxChars.ShouldBe(8000);
        settings.AnalysisGroupingMode.ShouldBe(AnalysisGroupingMode.SenderSubject);
        settings.AnalysisRepresentativesPerGroup.ShouldBe(2);
        settings.AnalysisMinGroupSize.ShouldBe(5);
        settings.AnalysisDerivedConfidencePenalty.ShouldBe(0.2);
        settings.AnalysisClusterDistance.ShouldBe(0.3);
        settings.AnalysisMemoryShortCircuit.ShouldBeFalse();
        settings.AnalysisMemoryMinApprovals.ShouldBe(7);
        settings.BulkApproveThreshold.ShouldBe(0.95);
        settings.AutoArchiveOnActionDone.ShouldBeTrue();
        settings.AnalysisPromptTemplate.ShouldBe("Classify:\n\t{{emails}}");
    }

    [Fact]
    public async Task Grouping_mode_is_snake_case_in_the_api_and_the_stored_document()
    {
        var response = await PutJsonAsync("""{"analysisGroupingMode":"sender_subject"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("\"analysisGroupingMode\":\"sender_subject\"");
        await using var db = postgres.CreateDbContext();
        var stored = JsonNode.Parse((await db.Settings.SingleAsync(Ct)).Document).ShouldNotBeNull();
        stored["analysisGroupingMode"].ShouldNotBeNull().GetValue<string>().ShouldBe("sender_subject");
    }

    [Theory]
    [InlineData("\"grouped\"")]
    [InlineData("1")]
    public async Task Unknown_grouping_mode_gets_400(string value)
    {
        var response = await PutJsonAsync($$"""{"analysisGroupingMode":{{value}}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GetAsync(factory)).AnalysisGroupingMode.ShouldBe(AnalysisGroupingMode.Auto);
    }

    public static TheoryData<string, string> AnalysisEdgeValues => new()
    {
        { "analysisDefaultCount", "1" }, { "analysisDefaultCount", "1000" },
        { "analysisBodyMaxChars", "500" }, { "analysisBodyMaxChars", "50000" },
        { "analysisRepresentativesPerGroup", "1" }, { "analysisRepresentativesPerGroup", "10" },
        { "analysisMinGroupSize", "2" }, { "analysisMinGroupSize", "50" },
        { "analysisDerivedConfidencePenalty", "0" }, { "analysisDerivedConfidencePenalty", "0.5" },
        { "analysisClusterDistance", "0.02" }, { "analysisClusterDistance", "0.6" },
        { "analysisMemoryMinApprovals", "1" }, { "analysisMemoryMinApprovals", "20" },
        { "bulkApproveThreshold", "0.5" }, { "bulkApproveThreshold", "1.0" },
    };

    [Theory]
    [MemberData(nameof(AnalysisEdgeValues))]
    public async Task Analysis_edge_values_are_accepted(string field, string value)
    {
        var response = await PutJsonAsync($$"""{"{{field}}":{{value}}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct)).ShouldNotBeNull()[field].ShouldNotBeNull();
        saved.GetValue<double>().ShouldBe(double.Parse(value, CultureInfo.InvariantCulture));
    }

    public static TheoryData<string, string> AnalysisOutOfRangeValues => new()
    {
        { "analysisDefaultCount", "0" }, { "analysisDefaultCount", "1001" },
        { "analysisBodyMaxChars", "499" }, { "analysisBodyMaxChars", "50001" },
        { "analysisRepresentativesPerGroup", "0" }, { "analysisRepresentativesPerGroup", "11" },
        { "analysisMinGroupSize", "1" }, { "analysisMinGroupSize", "51" },
        { "analysisDerivedConfidencePenalty", "-0.01" }, { "analysisDerivedConfidencePenalty", "0.51" },
        { "analysisClusterDistance", "0.019" }, { "analysisClusterDistance", "0.61" },
        { "analysisMemoryMinApprovals", "0" }, { "analysisMemoryMinApprovals", "21" },
        { "bulkApproveThreshold", "0.49" }, { "bulkApproveThreshold", "1.01" },
    };

    [Theory]
    [MemberData(nameof(AnalysisOutOfRangeValues))]
    public async Task Analysis_out_of_range_values_get_400_field_errors(string field, string value)
    {
        var response = await PutJsonAsync($$"""{"{{field}}":{{value}}}""");

        await ShouldBeValidationProblemAsync(response, field);
        await using var db = postgres.CreateDbContext();
        (await db.Settings.AnyAsync(Ct)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n\t ")]
    public async Task Blank_prompt_template_clears_the_override(string blank)
    {
        await PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, null, null, null, AnalysisPromptTemplate: "Custom prompt {{emails}}"));
        await PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, null, null, null));
        (await GetAsync(factory)).AnalysisPromptTemplate.ShouldBe("Custom prompt {{emails}}");

        var response = await PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, null, null, null, AnalysisPromptTemplate: blank));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(factory)).AnalysisPromptTemplate.ShouldBeNull();
    }

    [Fact]
    public async Task Prompt_template_at_max_length_is_accepted()
    {
        var template = "{{emails}}" + new string('p', SettingsValidation.MaxAnalysisPromptTemplateLength - "{{emails}}".Length);

        var response = await PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, null, null, null, AnalysisPromptTemplate: template));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(factory)).AnalysisPromptTemplate.ShouldBe(template);
    }

    public static TheoryData<string> InvalidPromptTemplates => new()
    {
        new string('p', SettingsValidation.MaxAnalysisPromptTemplateLength + 1),
        "Classify\u0000this",
        "Classify\u001bthis",
        "Classify without the emails placeholder",
    };

    [Theory]
    [MemberData(nameof(InvalidPromptTemplates))]
    public async Task Invalid_prompt_template_gets_400_field_error(string template)
    {
        var response = await PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, null, null, null, AnalysisPromptTemplate: template));

        await ShouldBeValidationProblemAsync(response, "analysisPromptTemplate");
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

    [Fact]
    public async Task Concurrent_updates_on_first_insert_lose_nothing()
    {
        var firstInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The first update holds its snapshot while the second starts; without a row lock the second
        // would read the same (empty) snapshot and one change would be lost, or its insert would hit the PK.
        var first = UpdateInScopeAsync(s =>
        {
            firstInside.TrySetResult();
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            return s with { ChatModel = "chat-model-a" };
        });
        await firstInside.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = UpdateInScopeAsync(s => s with { SetupWizardSeen = true });
        await Task.WhenAll(first, second);

        var settings = await GetAsync(factory);
        settings.ChatModel.ShouldBe("chat-model-a");
        settings.SetupWizardSeen.ShouldBeTrue();
    }

    [Fact]
    public async Task Concurrent_puts_all_succeed_and_all_changes_persist()
    {
        var responses = await Task.WhenAll(
            PutAsync(factory, "/api/settings/google-client", new GoogleClientRequest(ClientId, ClientSecret)),
            PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, null, null, true)),
            PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, "chat-model-a", null, null)),
            PutAsync(factory, "/api/settings", new UpdateSettingsRequest(null, null, "embed-model-a", null)),
            PutAsync(factory, "/api/settings", new UpdateSettingsRequest("https://llm.example.com/", null, null, null)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        var settings = await GetAsync(factory);
        settings.GoogleClient.ShouldBe(new GoogleClientDto(ClientId, SecretSet: true, LockedByEnv: false));
        settings.SetupWizardSeen.ShouldBeTrue();
        settings.ChatModel.ShouldBe("chat-model-a");
        settings.EmbeddingModel.ShouldBe("embed-model-a");
        settings.OllamaBaseUrl.ShouldBe("https://llm.example.com/");
    }

    private async Task UpdateInScopeAsync(Func<AppSettings, AppSettings> change)
    {
        await Task.Yield();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
        await store.UpdateAsync(change, Ct);
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

    private static async Task ShouldBeValidationProblemAsync(HttpResponseMessage response, string? field = null)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        if (field is not null)
        {
            var problem = (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull();
            problem.Errors.Keys.ShouldBe([field]);
        }
    }

    private Task<HttpResponseMessage> PutJsonAsync(string json) =>
        PutAsync(factory, "/api/settings", JsonNode.Parse(json));

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
