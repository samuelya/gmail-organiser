using System.Text.Json;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace GmailOrganiser.Tests.Unit;

/// <summary>The Claude API key (#485): encrypted at rest, hint only, lost key ring is "not set", no value in any log.</summary>
public sealed class ClaudeApiKeyServiceTests
{
    // Built, not a literal: a key-shaped literal trips the secret scan.
    private static readonly string SyntheticKey = new string('k', 30) + "WXYZ";

    private readonly InMemorySettingsStore settings = new();
    private readonly EphemeralDataProtectionProvider dataProtection = new();
    private readonly CapturingLogger logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClaudeApiKeyService Create(IDataProtectionProvider? provider = null) => new(settings, provider ?? dataProtection, logger);

    [Fact]
    public async Task Set_stores_ciphertext_and_get_decrypts_it()
    {
        var service = Create();

        await service.SetAsync(SyntheticKey, Ct);

        settings.Current.ClaudeApiKeyProtected.ShouldNotBeNullOrEmpty();
        settings.Current.ClaudeApiKeyProtected.ShouldNotContain(SyntheticKey);
        (await service.GetAsync(Ct)).ShouldBe(SyntheticKey);
        (await service.HintAsync(settings.Current, Ct)).ShouldBe("…WXYZ");
        logger.Entries.ShouldBe([(LogLevel.Information, "Claude API key saved")]);
    }

    [Fact]
    public async Task Clear_removes_the_key_and_is_idempotent()
    {
        var service = Create();
        await service.SetAsync(SyntheticKey, Ct);

        await service.ClearAsync(Ct);
        await service.ClearAsync(Ct);

        settings.Current.ClaudeApiKeyProtected.ShouldBeNull();
        (await service.GetAsync(Ct)).ShouldBeNull();
        (await service.HintAsync(settings.Current, Ct)).ShouldBeNull();
        logger.Entries.ShouldBe([(LogLevel.Information, "Claude API key saved"), (LogLevel.Information, "Claude API key cleared")]);
    }

    [Fact]
    public async Task Clear_without_a_key_writes_and_logs_nothing()
    {
        await Create().ClearAsync(Ct);

        settings.UpdateCount.ShouldBe(0);
        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Undecryptable_ciphertext_is_removed_with_one_warning_that_names_no_value()
    {
        await Create().SetAsync(SyntheticKey, Ct);
        var stale = settings.Current;
        var ciphertext = stale.ClaudeApiKeyProtected!;
        logger.Entries.Clear();

        // A fresh ephemeral provider has a different key ring: the saved ciphertext no longer decrypts.
        var service = Create(new EphemeralDataProtectionProvider());

        (await service.GetAsync(Ct)).ShouldBeNull();
        (await service.HintAsync(stale, Ct)).ShouldBeNull();
        (await service.GetAsync(Ct)).ShouldBeNull();
        settings.Current.ClaudeApiKeyProtected.ShouldBeNull();
        logger.Entries.Count.ShouldBe(1);
        logger.Entries[0].Level.ShouldBe(LogLevel.Warning);
        logger.Entries[0].Message.ShouldNotContain(SyntheticKey);
        logger.Entries[0].Message.ShouldNotContain(ciphertext);
    }

    [Fact]
    public async Task A_stale_read_does_not_remove_a_key_saved_since()
    {
        await Create().SetAsync(SyntheticKey, Ct);
        var stale = settings.Current;
        var service = Create(new EphemeralDataProtectionProvider());
        await service.SetAsync(SyntheticKey, Ct);
        logger.Entries.Clear();

        (await service.HintAsync(stale, Ct)).ShouldBeNull();

        (await service.GetAsync(Ct)).ShouldBe(SyntheticKey);
        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Active_chat_model_and_meter_num_ctx_follow_the_provider_and_are_not_stored()
    {
        var ollama = new AppSettings { ChatModel = "chat-model-a", ClaudeApiModel = "api-model-a", LlmNumCtx = 4096 };
        var claude = ollama with { LlmProvider = LlmProvider.ClaudeApi };

        ollama.ActiveChatModel.ShouldBe("chat-model-a");
        ollama.MeterNumCtx.ShouldBe(4096);
        claude.ActiveChatModel.ShouldBe("api-model-a");
        claude.MeterNumCtx.ShouldBe(0);
        var json = JsonSerializer.Serialize(claude, JsonSerializerOptions.Web);
        json.ShouldContain("\"llmProvider\":\"claude_api\"");
        json.ShouldNotContain("activeChatModel");
        json.ShouldNotContain("meterNumCtx");
    }

    [Fact]
    public void Old_documents_load_with_the_defaults()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("""{"chatModel":"chat-model-a"}""", JsonSerializerOptions.Web).ShouldNotBeNull();

        loaded.LlmProvider.ShouldBe(LlmProvider.Ollama);
        loaded.ClaudeApiModel.ShouldBeNull();
        loaded.ClaudeApiKeyProtected.ShouldBeNull();
        loaded.ActiveChatModel.ShouldBe("chat-model-a");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short-key-0123456")]
    [InlineData("synthetic key with spaces 0123")]
    [InlineData("synthetic-claude-api-key-é-0123")]
    [InlineData("synthetic-claude-api-key-\u0001-0123")]
    public void Invalid_keys_get_a_field_error_without_the_value(string? key)
    {
        var (normalised, errors) = ClaudeApiValidation.Validate(new SetClaudeApiKeyRequest(key));

        normalised.ShouldBeNull();
        errors.ShouldNotBeNull().Keys.ShouldBe(["apiKey"]);
        if (!string.IsNullOrEmpty(key))
        {
            errors!["apiKey"].ShouldAllBe(m => !m.Contains(key));
        }
    }

    [Fact]
    public void Boundary_lengths_and_surrounding_whitespace_are_accepted()
    {
        ClaudeApiValidation.Validate(new SetClaudeApiKeyRequest(new string('k', ClaudeApiValidation.MinKeyLength))).Key.ShouldNotBeNull();
        ClaudeApiValidation.Validate(new SetClaudeApiKeyRequest(new string('k', ClaudeApiValidation.MaxKeyLength))).Key.ShouldNotBeNull();
        ClaudeApiValidation.Validate(new SetClaudeApiKeyRequest($"  {SyntheticKey}\n")).ShouldBe((SyntheticKey, null));
        ClaudeApiValidation.Validate(new SetClaudeApiKeyRequest(new string('k', ClaudeApiValidation.MaxKeyLength + 1))).Errors!.Keys.ShouldBe(["apiKey"]);
    }

    private sealed class CapturingLogger : ILogger<ClaudeApiKeyService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
