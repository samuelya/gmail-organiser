using GmailOrganiser.Llm;
using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace GmailOrganiser.Tests.Unit;

public sealed class LlmClientFactoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (LlmClientFactory Factory, InMemorySettingsStore Settings, StubHttpClientFactory Http) Create(
        HttpMessageHandler? handler = null, bool useFake = false)
    {
        var settings = new InMemorySettingsStore();
        var http = new StubHttpClientFactory(handler ?? new StubOllamaHandler());
        var options = Options.Create(new LlmOptions { UseFake = useFake });
        return (new LlmClientFactory(http, settings, TestClaudeApiKeys.For(settings), options), settings, http);
    }

    private static async Task<(LlmClientFactory Factory, InMemorySettingsStore Settings, StubHttpClientFactory Http)> CreateClaudeApi(
        FakeAnthropicHandler api, string? model, string? apiKey, bool useFake = false)
    {
        var (factory, settings, http) = Create(api, useFake);
        settings.Current = settings.Current with { ChatModel = "test-chat:1b", LlmProvider = LlmProvider.ClaudeApi, ClaudeApiModel = model };
        if (apiKey is not null)
        {
            await TestClaudeApiKeys.For(settings).SetAsync(apiKey, Ct);
        }

        return (factory, settings, http);
    }

    [Fact]
    public async Task Claude_api_without_a_model_throws_not_configured_even_with_an_ollama_model()
    {
        var (factory, _, _) = await CreateClaudeApi(new FakeAnthropicHandler(), model: null, apiKey: "sk-test-synthetic");

        var ex = await Should.ThrowAsync<LlmNotConfiguredException>(() => factory.CreateChatClientAsync(Ct));

        ex.Kind.ShouldBe(ModelKinds.Chat);
    }

    [Fact]
    public async Task Claude_api_without_a_key_throws_not_configured_with_a_key_message()
    {
        var (factory, _, _) = await CreateClaudeApi(new FakeAnthropicHandler(), "test-model-a", apiKey: null);

        var ex = await Should.ThrowAsync<LlmNotConfiguredException>(() => factory.CreateChatClientAsync(Ct));

        ex.Kind.ShouldBe(ModelKinds.Chat);
        ex.Message.ShouldBe("No Claude API key is set. Add one in Settings.");
    }

    [Fact]
    public async Task Claude_api_calls_the_messages_api_with_the_saved_key_and_model()
    {
        var api = new FakeAnthropicHandler().Message("ok");
        var (factory, _, http) = await CreateClaudeApi(api, "test-model-a", "sk-test-synthetic");

        using var chat = await factory.CreateChatClientAsync(Ct);
        var response = await chat.GetResponseAsync("Synthetic prompt", cancellationToken: Ct);

        response.Text.ShouldBe("ok");
        http.Names.ShouldBe([ClaudeApiHttp.ClientName]);
        var request = api.Requests.ShouldHaveSingleItem();
        request.Headers["x-api-key"].ShouldBe("sk-test-synthetic");
        request.Body.GetProperty("model").GetString().ShouldBe("test-model-a");
    }

    [Fact]
    public async Task With_llm_fake_both_claude_api_paths_return_the_fake_client()
    {
        var api = new FakeAnthropicHandler();
        var (factory, _, http) = await CreateClaudeApi(api, "test-model-a", apiKey: null, useFake: true);

        using var fromSettings = await factory.CreateChatClientAsync(Ct);
        using var explicitClient = factory.CreateClaudeApiChatClient("sk-test-synthetic", "test-model-a");

        fromSettings.ShouldBeOfType<FakeChatClient>();
        explicitClient.ShouldBeOfType<FakeChatClient>();
        http.Names.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_chat_model_it_throws_not_configured()
    {
        var (factory, _, _) = Create();

        var ex = await Should.ThrowAsync<LlmNotConfiguredException>(() => factory.CreateChatClientAsync(Ct));

        ex.Kind.ShouldBe(ModelKinds.Chat);
    }

    [Fact]
    public async Task Without_an_embedding_model_it_throws_not_configured()
    {
        var (factory, settings, _) = Create();
        settings.Current = settings.Current with { ChatModel = "test-chat:1b" };

        var ex = await Should.ThrowAsync<LlmNotConfiguredException>(() => factory.CreateEmbeddingGeneratorAsync(Ct));

        ex.Kind.ShouldBe(ModelKinds.Embedding);
    }

    [Fact]
    public async Task Picks_up_a_settings_change_without_a_restart()
    {
        var (factory, settings, http) = Create();
        settings.Current = new AppSettings { OllamaBaseUrl = "http://one.example.com:11434", ChatModel = "test-chat:1b", EmbeddingModel = "test-embed" };

        using var firstClient = await factory.CreateChatClientAsync(Ct);
        settings.Current = settings.Current with { OllamaBaseUrl = "http://two.example.com:11434/base", ChatModel = "test-chat:2b" };
        using var secondClient = await factory.CreateChatClientAsync(Ct);
        using var embed = (OllamaApiClient)await factory.CreateEmbeddingGeneratorAsync(Ct);

        var first = firstClient.GetService<OllamaApiClient>().ShouldNotBeNull();
        var second = secondClient.GetService<OllamaApiClient>().ShouldNotBeNull();
        first.SelectedModel.ShouldBe("test-chat:1b");
        first.Uri.ShouldBe(new Uri("http://one.example.com:11434/"));
        second.SelectedModel.ShouldBe("test-chat:2b");
        second.Uri.ShouldBe(new Uri("http://two.example.com:11434/base/"));
        embed.SelectedModel.ShouldBe("test-embed");
        http.Names.ShouldAllBe(n => n == OllamaHttp.ClientName);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("3601")]
    public void An_out_of_range_model_timeout_fails_validation(string value)
    {
        using var services = LlmServices(value);

        var ex = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<LlmOptions>>().Value);

        ex.Message.ShouldContain("Llm__ModelTimeoutSeconds");
    }

    [Fact]
    public void A_non_numeric_model_timeout_fails_at_bind()
    {
        using var services = LlmServices("3m");

        Should.Throw<InvalidOperationException>(() => services.GetRequiredService<IOptions<LlmOptions>>().Value);
    }

    [Fact]
    public void A_model_timeout_in_range_binds()
    {
        using var services = LlmServices("600");

        services.GetRequiredService<IOptions<LlmOptions>>().Value.ModelTimeout.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Theory]
    [InlineData("Llm:ClaudeApiMaxRetries", "11", "Llm__ClaudeApiMaxRetries")]
    [InlineData("Llm:ClaudeApiTimeoutSeconds", "0", "Llm__ClaudeApiTimeoutSeconds")]
    [InlineData("Llm:ClaudeApiMaxOutputTokens", "0", "Llm__ClaudeApiMaxOutputTokens")]
    public void Out_of_range_claude_api_options_fail_validation(string key, string value, string expected)
    {
        using var services = LlmServices(value, key);

        var ex = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<LlmOptions>>().Value);

        ex.Message.ShouldContain(expected);
    }

    private static ServiceProvider LlmServices(string modelTimeoutSeconds, string key = "Llm:ModelTimeoutSeconds")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = modelTimeoutSeconds })
            .Build();
        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLlm()
            .BuildServiceProvider();
    }
}
