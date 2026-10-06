using GmailOrganiser.Llm;
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

    private static (LlmClientFactory Factory, InMemorySettingsStore Settings, StubHttpClientFactory Http) Create()
    {
        var settings = new InMemorySettingsStore();
        var http = new StubHttpClientFactory(new StubOllamaHandler());
        return (new LlmClientFactory(http, settings, Options.Create(new LlmOptions())), settings, http);
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

    private static ServiceProvider LlmServices(string modelTimeoutSeconds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:ModelTimeoutSeconds"] = modelTimeoutSeconds })
            .Build();
        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLlm()
            .BuildServiceProvider();
    }
}
