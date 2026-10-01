using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using GmailOrganiser.Common;
using GmailOrganiser.Llm;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class LlmEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string NotConfiguredPath = "/api/test-only/llm-chat";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Models_are_split_by_capability_with_the_fallback_in_both_lists()
    {
        var stub = StubOllamaHandler.WithModels(
            ("test-chat:1b", ["completion", "tools"]), ("test-embed", ["embedding"]), ("test-unknown", null));
        await using var host = Host(stub);

        var models = await host.CreateClient().GetFromJsonAsync<LlmModelsDto>(
            "/api/llm/models?baseUrl=" + Uri.EscapeDataString("http://wizard.example.com:11434"), Ct);

        models!.Reachable.ShouldBeTrue();
        models.Version.ShouldBe("0.0.1-test");
        models.Error.ShouldBeNull();
        models.ChatModels.Select(m => m.Name).ShouldBe(["test-chat:1b", "test-unknown"]);
        models.EmbeddingModels.Select(m => m.Name).ShouldBe(["test-embed", "test-unknown"]);
        stub.Requests.ShouldAllBe(r => r.Uri.Host == "wizard.example.com");
    }

    [Fact]
    public async Task Models_use_the_saved_url_by_default()
    {
        var stub = StubOllamaHandler.WithModels(("test-chat:1b", ["completion"]));
        await using var host = Host(stub);

        var models = await host.CreateClient().GetFromJsonAsync<LlmModelsDto>("/api/llm/models", Ct);

        models!.Reachable.ShouldBeTrue();
        stub.Requests.ShouldAllBe(r => r.Uri.GetLeftPart(UriPartial.Authority) == ApiFactory.EnvOllamaBaseUrl);
    }

    [Fact]
    public async Task Pre_release_version_still_lists_the_models()
    {
        var stub = StubOllamaHandler.WithModels(("test-chat:1b", ["completion"]))
            .Json("/api/version", new { version = "0.12.0-rc1" });
        await using var host = Host(stub);

        var models = await host.CreateClient().GetFromJsonAsync<LlmModelsDto>("/api/llm/models", Ct);

        models!.Reachable.ShouldBeTrue();
        models.Version.ShouldBe("0.12.0-rc1");
        models.ChatModels.Select(m => m.Name).ShouldBe(["test-chat:1b"]);
    }

    [Fact]
    public async Task Unreachable_server_is_200_with_reachable_false()
    {
        var stub = new StubOllamaHandler
        {
            Failure = new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)),
        };
        await using var host = Host(stub);

        var response = await host.CreateClient().GetAsync("/api/llm/models", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var models = await response.Content.ReadFromJsonAsync<LlmModelsDto>(Ct);
        models!.Reachable.ShouldBeFalse();
        models.Error!.ShouldContain("Connection refused");
        models.ChatModels.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ftp://ollama.example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    [InlineData("/relative")]
    public async Task Models_reject_a_non_http_base_url(string baseUrl)
    {
        await using var host = Host(new StubOllamaHandler());

        var response = await host.CreateClient().GetAsync("/api/llm/models?baseUrl=" + Uri.EscapeDataString(baseUrl), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.ShouldContainKey("baseUrl");
    }

    [Fact]
    public async Task Test_model_chat_success_uses_the_given_url_and_model()
    {
        var fake = new FakeLlmClientFactory();
        await using var host = Host(new StubOllamaHandler(), fake);

        var response = await PostAsync(host, new TestModelRequest("chat", " test-chat:1b ", "http://wizard.example.com:11434"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<TestModelResultDto>(Ct);
        result!.Ok.ShouldBeTrue();
        fake.Targets.ShouldBe([(new Uri("http://wizard.example.com:11434"), "test-chat:1b")]);
    }

    [Fact]
    public async Task Test_model_embedding_failure_is_ok_false()
    {
        var fake = new FakeLlmClientFactory();
        fake.Embeddings.Failure = new HttpRequestException("model 'test-embed' not found", null, HttpStatusCode.NotFound);
        await using var host = Host(new StubOllamaHandler(), fake);

        var response = await PostAsync(host, new TestModelRequest("embedding", "test-embed", null));

        var result = await response.Content.ReadFromJsonAsync<TestModelResultDto>(Ct);
        result!.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("not found");
        fake.Targets.Single().BaseUrl.ShouldBe(new Uri(ApiFactory.EnvOllamaBaseUrl));
    }

    [Theory]
    [InlineData("vision", "test-chat:1b", null, "kind")]
    [InlineData("chat", "", null, "model")]
    [InlineData("chat", "test-chat:1b", "javascript:alert(1)", "baseUrl")]
    public async Task Test_model_validates_input(string kind, string model, string? baseUrl, string field)
    {
        await using var host = Host(new StubOllamaHandler(), new FakeLlmClientFactory());

        var response = await PostAsync(host, new TestModelRequest(kind, model, baseUrl));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.ShouldContainKey(field);
    }

    [Fact]
    public async Task Missing_model_setting_is_409_problem_details()
    {
        await using var host = Host(new StubOllamaHandler());

        var response = await host.CreateClient().GetAsync(NotConfiguredPath, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        problem!.Status.ShouldBe(409);
        problem.Detail!.ShouldContain("chat");
    }

    private WebApplicationFactory<Program> Host(StubOllamaHandler stub, ILlmClientFactory? llm = null) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddHttpClient(OllamaHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => stub);
            if (llm is not null)
            {
                services.RemoveAll<ILlmClientFactory>();
                services.AddSingleton(llm);
            }

            services.AddTransient<IStartupFilter, NotConfiguredEndpointFilter>();
        }));

    private static async Task<HttpResponseMessage> PostAsync(WebApplicationFactory<Program> host, TestModelRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/llm/test-model") { Content = JsonContent.Create(body) };
        request.Headers.Add(ApiRequestGuardMiddleware.RequestedWithHeader, "XMLHttpRequest");
        return await host.CreateClient().SendAsync(request, Ct);
    }

    /// <summary>A test-only endpoint that asks the real factory for a chat client.</summary>
    private sealed class NotConfiguredEndpointFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Map(NotConfiguredPath, branch => branch.Run(async context =>
            {
                using var client = await context.RequestServices.GetRequiredService<ILlmClientFactory>().CreateChatClientAsync();
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            }));
        };
    }
}
