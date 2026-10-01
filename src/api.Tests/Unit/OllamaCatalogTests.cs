using System.Net;
using System.Net.Sockets;
using GmailOrganiser.Llm;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit;

public sealed class OllamaCatalogTests
{
    private const string SavedUrl = "http://saved.example.com:11434";
    private const string OtherUrl = "http://other.example.com:11434";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OllamaCatalog Catalog(StubOllamaHandler stub, int timeoutSeconds = 10) => new(
        new StubHttpClientFactory(stub),
        new InMemorySettingsStore(new AppSettings { OllamaBaseUrl = SavedUrl }),
        Options.Create(new LlmOptions { CatalogTimeoutSeconds = timeoutSeconds }),
        NullLogger<OllamaCatalog>.Instance);

    [Fact]
    public async Task Lists_models_with_details_and_capabilities()
    {
        var stub = StubOllamaHandler.WithModels(("test-chat:1b", ["completion", "tools"]), ("test-embed", ["embedding"]));

        var models = await Catalog(stub).ListModelsAsync(ct: Ct);

        models.Select(m => m.Name).ShouldBe(["test-chat:1b", "test-embed"]);
        models[0].ShouldBe(models[0] with { SizeBytes = 1_000_000, Family = "testfamily", ParameterSize = "1B" });
        models[0].Capabilities.ShouldBe(["completion", "tools"]);
        models[1].Capabilities.ShouldBe(["embedding"]);
        stub.Requests.ShouldAllBe(r => r.Uri.Host == "saved.example.com");
    }

    [Fact]
    public async Task Failing_show_lists_the_model_without_capabilities()
    {
        var stub = StubOllamaHandler.WithModels(("test-chat:1b", ["completion"]), ("test-unknown", null));

        var models = await Catalog(stub).ListModelsAsync(ct: Ct);

        models.Single(m => m.Name == "test-unknown").Capabilities.ShouldBeEmpty();
    }

    [Fact]
    public async Task Shows_run_at_most_four_at_a_time()
    {
        var names = Enumerable.Range(0, 10).Select(i => ($"test-model-{i}", (string[]?)["completion"])).ToArray();
        var stub = StubOllamaHandler.WithModels(names);
        var current = 0;
        var peak = 0;
        stub.Route("/api/show", async (request, body) =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref peak, now);
            await Task.Delay(20, Ct);
            Interlocked.Decrement(ref current);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"capabilities":["completion"]}""", System.Text.Encoding.UTF8, "application/json"),
            };
        });

        var models = await Catalog(stub).ListModelsAsync(ct: Ct);

        models.Count.ShouldBe(10);
        peak.ShouldBeInRange(1, OllamaCatalog.MaxParallelShows);
    }

    [Fact]
    public async Task Ping_returns_the_version_from_an_explicit_url()
    {
        var stub = StubOllamaHandler.WithModels();

        var version = await Catalog(stub).PingAsync(OtherUrl, Ct);

        version.ShouldBe("0.0.1-test");
        stub.Requests.Single().Uri.ShouldBe(new Uri(OtherUrl + "/api/version"));
    }

    [Fact]
    public async Task Ping_keeps_a_pre_release_version_string()
    {
        var stub = new StubOllamaHandler().Json("/api/version", new { version = "0.12.0-rc1" });

        var version = await Catalog(stub).PingAsync(ct: Ct);

        version.ShouldBe("0.12.0-rc1");
    }

    [Fact]
    public async Task Ping_without_a_version_field_returns_an_empty_version()
    {
        var stub = new StubOllamaHandler().Json("/api/version", new { other = "x" });

        var version = await Catalog(stub).PingAsync(ct: Ct);

        version.ShouldBe("");
    }

    [Fact]
    public async Task Ping_with_a_non_json_answer_is_a_readable_error()
    {
        var stub = new StubOllamaHandler().Route("/api/version", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html></html>", System.Text.Encoding.UTF8, "text/html"),
        }));

        var ex = await Should.ThrowAsync<OllamaUnreachableException>(() => Catalog(stub).PingAsync(ct: Ct));

        ex.Message.ShouldContain("not like an Ollama server");
    }

    [Fact]
    public async Task Connection_refused_is_a_readable_error_with_the_localhost_hint()
    {
        var stub = new StubOllamaHandler
        {
            Failure = new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)),
        };

        var ex = await Should.ThrowAsync<OllamaUnreachableException>(() => Catalog(stub).PingAsync(ct: Ct));

        ex.Message.ShouldContain("Connection refused");
        ex.Message.ShouldContain("http://localhost:11434");
    }

    [Fact]
    public async Task Timeout_is_a_readable_error()
    {
        var stub = new StubOllamaHandler { Hang = true };

        var ex = await Should.ThrowAsync<OllamaUnreachableException>(() => Catalog(stub, timeoutSeconds: 1).ListModelsAsync(ct: Ct));

        ex.Message.ShouldContain("No answer");
        ex.Message.ShouldContain("1 s");
    }

    [Fact]
    public async Task A_server_that_is_not_ollama_is_a_readable_error()
    {
        var stub = new StubOllamaHandler(); // every path is 404

        var ex = await Should.ThrowAsync<OllamaUnreachableException>(() => Catalog(stub).PingAsync(ct: Ct));

        ex.Message.ShouldContain("not");
        ex.Message.ShouldContain("Ollama");
    }

    [Fact]
    public void Split_filters_by_capability_and_falls_back_to_both_lists()
    {
        OllamaModelDto Model(string name, params string[] caps) => new(name, 1, null, null, caps);
        var models = new[]
        {
            Model("test-chat:1b", "completion", "tools"),
            Model("test-embed", "embedding"),
            Model("test-unknown"),
            Model("test-vision-only", "vision"),
        };

        var split = LlmModelsDto.Split("0.0.1-test", models);

        split.Reachable.ShouldBeTrue();
        split.ChatModels.Select(m => m.Name).ShouldBe(["test-chat:1b", "test-unknown"]);
        split.EmbeddingModels.Select(m => m.Name).ShouldBe(["test-embed", "test-unknown"]);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
