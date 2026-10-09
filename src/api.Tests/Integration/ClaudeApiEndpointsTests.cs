using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Common;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The Claude API model list and model test (#489), against a fake Anthropic API or <c>LLM_FAKE=true</c>.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ClaudeApiEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    // Built, not a literal: a key-shaped literal trips the secret scan.
    private static readonly string SyntheticKey = new string('k', 30) + "WXYZ";
    private const string ModelsPath = "/api/llm/claude-api/models";
    private const string TestPath = "/api/llm/claude-api/test-model";

    private readonly FakeAnthropicHandler api = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task No_key_lists_nothing_and_calls_nobody()
    {
        await using var host = Host();

        var models = await host.CreateClient().GetFromJsonAsync<ClaudeApiModelsDto>(ModelsPath, Ct);

        models!.KeySet.ShouldBeFalse();
        models.Models.ShouldBeEmpty();
        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Key_set_lists_the_models_newest_first_without_the_key()
    {
        api.Models(false, ("test-model-a", "Test Model A", "2026-01-01T00:00:00Z"), ("test-model-b", "Test Model B", "2026-03-01T00:00:00Z"));
        await using var host = Host();
        await SetKeyAsync(host);

        var response = await host.CreateClient().GetAsync(ModelsPath, Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain(SyntheticKey);
        var models = (await response.Content.ReadFromJsonAsync<ClaudeApiModelsDto>(Ct)).ShouldNotBeNull();
        models.KeySet.ShouldBeTrue();
        models.Reachable.ShouldBeTrue();
        models.Models.Select(m => m.Id).ShouldBe(["test-model-b", "test-model-a"]);
        api.Requests.ShouldHaveSingleItem().Headers["x-api-key"].ShouldBe(SyntheticKey);
    }

    [Fact]
    public async Task Rejected_key_is_a_readable_200()
    {
        api.Error(HttpStatusCode.Unauthorized, "authentication_error");
        await using var host = Host();
        await SetKeyAsync(host);

        var response = await host.CreateClient().GetAsync(ModelsPath, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain(SyntheticKey);
        var models = (await response.Content.ReadFromJsonAsync<ClaudeApiModelsDto>(Ct)).ShouldNotBeNull();
        models.Reachable.ShouldBeFalse();
        models.Error.ShouldBe("The Claude API rejected the API key (HTTP 401). Check the key in Settings.");
    }

    [Fact]
    public async Task Test_model_runs_the_json_prompt_through_the_claude_api_client()
    {
        api.Message("""{"ok":true}""");
        await using var host = Host();
        await SetKeyAsync(host);

        var response = await PostAsync(host, new TestClaudeApiModelRequest(" test-model-a "));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<TestModelResultDto>(Ct))!.Ok.ShouldBeTrue();
        var request = api.Requests.ShouldHaveSingleItem();
        request.Path.ShouldBe("/v1/messages");
        request.Body.GetProperty("model").GetString().ShouldBe("test-model-a");
    }

    [Fact]
    public async Task Test_model_error_is_a_key_free_result()
    {
        api.Error(HttpStatusCode.NotFound, "not_found_error");
        await using var host = Host();
        await SetKeyAsync(host);

        var response = await PostAsync(host, new TestClaudeApiModelRequest("test-model-b"));

        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain(SyntheticKey);
        var result = (await response.Content.ReadFromJsonAsync<TestModelResultDto>(Ct)).ShouldNotBeNull();
        result.Ok.ShouldBeFalse();
        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("404");
        error.Length.ShouldBeLessThanOrEqualTo(330);
    }

    [Fact]
    public async Task Test_model_reports_a_rate_limit_after_one_attempt()
    {
        api.Error(HttpStatusCode.TooManyRequests, "rate_limit_error", ("retry-after", "30"));
        await using var host = Host();
        await SetKeyAsync(host);

        var response = await PostAsync(host, new TestClaudeApiModelRequest("test-model-a"));

        var result = (await response.Content.ReadFromJsonAsync<TestModelResultDto>(Ct)).ShouldNotBeNull();
        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("rate limited or overloaded (HTTP 429)");
        api.Requests.Count.ShouldBe(1);
    }

    public static TheoryData<string> InvalidModels => new() { "", "  ", "model\nname", new string('m', SettingsValidation.MaxModelNameLength + 1) };

    [Theory]
    [MemberData(nameof(InvalidModels))]
    public async Task Invalid_model_is_400(string model)
    {
        await using var host = Host();
        await SetKeyAsync(host);

        var response = await PostAsync(host, new TestClaudeApiModelRequest(model));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct))!.Errors.Keys.ShouldBe(["model"]);
        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Test_model_without_a_key_is_409()
    {
        await using var host = Host();

        var response = await PostAsync(host, new TestClaudeApiModelRequest("test-model-a"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title.ShouldBe("LLM not configured");
        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Fake_llm_lists_the_fake_model_only_with_a_key_and_tests_it()
    {
        await using var host = Host(fake: true);

        (await host.CreateClient().GetFromJsonAsync<ClaudeApiModelsDto>(ModelsPath, Ct))!.Models.ShouldBeEmpty();
        await SetKeyAsync(host);
        var models = (await host.CreateClient().GetFromJsonAsync<ClaudeApiModelsDto>(ModelsPath, Ct)).ShouldNotBeNull();
        var test = await PostAsync(host, new TestClaudeApiModelRequest(FakeClaudeApiCatalog.Model));

        models.Models.Select(m => m.Id).ShouldBe([FakeClaudeApiCatalog.Model]);
        (await test.Content.ReadFromJsonAsync<TestModelResultDto>(Ct))!.Ok.ShouldBeTrue();
        api.Requests.ShouldBeEmpty();
    }

    private WebApplicationFactory<Program> Host(bool fake = false) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting(LlmOptions.FakeEnvironmentKey, fake ? "true" : "false");
            b.UseSetting("Llm:ClaudeApiMaxRetries", "0");
            b.UseSetting("Llm:ClaudeApiBaseUrl", "https://claude.example.com");
            b.ConfigureServices(s =>
            {
                s.AddHttpClient(ClaudeApiHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => api);
                s.AddHttpClient(ClaudeApiHttp.CatalogClientName).ConfigurePrimaryHttpMessageHandler(() => api);
                s.AddHttpClient(ClaudeApiHttp.TestClientName).ConfigurePrimaryHttpMessageHandler(() => api);
            });
        });

    private static async Task SetKeyAsync(WebApplicationFactory<Program> host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/llm/claude-api/key") { Content = JsonContent.Create(new { apiKey = SyntheticKey }) };
        request.Headers.Add(ApiRequestGuardMiddleware.RequestedWithHeader, "XMLHttpRequest");
        (await host.CreateClient().SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task<HttpResponseMessage> PostAsync(WebApplicationFactory<Program> host, TestClaudeApiModelRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TestPath) { Content = JsonContent.Create(body) };
        request.Headers.Add(ApiRequestGuardMiddleware.RequestedWithHeader, "XMLHttpRequest");
        return await host.CreateClient().SendAsync(request, Ct);
    }
}
