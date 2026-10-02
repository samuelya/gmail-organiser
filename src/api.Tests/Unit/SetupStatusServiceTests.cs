using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Llm;
using GmailOrganiser.Settings;
using GmailOrganiser.Setup;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Unit;

public sealed class SetupStatusServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider time = new();
    private readonly InMemorySettingsStore settings = new();
    private readonly FakeTokenStore tokens;
    private readonly StubCatalog ollama = new();
    private readonly StubAccountGuard accountGuard = new();
    private SettingsEnvOptions env = new();
    private bool useFakeGmail;

    public SetupStatusServiceTests() => tokens = new FakeTokenStore(time);

    [Fact]
    public async Task Fresh_install_reports_nothing_done()
    {
        await tokens.DeleteAsync(Ct);
        ollama.Fails = true;

        var status = await Create().GetAsync(Ct);

        status.ShouldBe(new SetupStatusDto(
            GoogleClientConfigured: false,
            GmailConnected: false,
            GmailReauthRequired: false,
            OllamaReachable: false,
            ChatModelSelected: false,
            EmbeddingModelSelected: false,
            WizardSeen: false,
            Complete: false,
            AccountMismatch: false));
    }

    [Fact]
    public async Task Complete_when_Gmail_is_connected_and_a_chat_model_is_selected_without_an_embedding_model()
    {
        settings.Current = settings.Current with { ChatModel = "test-chat" };

        var status = await Create().GetAsync(Ct);

        status.GmailConnected.ShouldBeTrue();
        status.ChatModelSelected.ShouldBeTrue();
        status.EmbeddingModelSelected.ShouldBeFalse();
        status.Complete.ShouldBeTrue();
    }

    [Fact]
    public async Task Not_complete_without_a_chat_model()
    {
        settings.Current = settings.Current with { ChatModel = " ", EmbeddingModel = "test-embed" };

        var status = await Create().GetAsync(Ct);

        status.ChatModelSelected.ShouldBeFalse();
        status.EmbeddingModelSelected.ShouldBeTrue();
        status.Complete.ShouldBeFalse();
    }

    [Fact]
    public async Task Not_complete_without_Gmail()
    {
        settings.Current = settings.Current with { ChatModel = "test-chat" };
        await tokens.DeleteAsync(Ct);

        var status = await Create().GetAsync(Ct);

        status.GmailConnected.ShouldBeFalse();
        status.GmailReauthRequired.ShouldBeFalse();
        status.Complete.ShouldBeFalse();
    }

    [Fact]
    public async Task Reauth_required_is_not_connected_and_not_complete()
    {
        settings.Current = settings.Current with { ChatModel = "test-chat" };
        await tokens.MarkReauthRequiredAsync(Ct);

        var status = await Create().GetAsync(Ct);

        status.GmailConnected.ShouldBeFalse();
        status.GmailReauthRequired.ShouldBeTrue();
        status.Complete.ShouldBeFalse();
    }

    [Fact]
    public async Task Wizard_seen_comes_from_the_settings()
    {
        settings.Current = settings.Current with { SetupWizardSeen = true };

        (await Create().GetAsync(Ct)).WizardSeen.ShouldBeTrue();
    }

    [Fact]
    public async Task Google_client_is_configured_from_the_env_values()
    {
        env = new SettingsEnvOptions { GoogleClientId = "client.example.com", GoogleClientSecret = "synthetic-secret" };

        (await Create().GetAsync(Ct)).GoogleClientConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task Google_client_without_a_secret_is_not_configured()
    {
        settings.Current = settings.Current with { GoogleClientId = "client.example.com" };

        (await Create().GetAsync(Ct)).GoogleClientConfigured.ShouldBeFalse();
    }

    [Fact]
    public async Task Fake_Gmail_needs_no_Google_client()
    {
        useFakeGmail = true;

        (await Create().GetAsync(Ct)).GoogleClientConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task Ollama_is_reachable_when_the_ping_answers()
    {
        (await Create().GetAsync(Ct)).OllamaReachable.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failing_ping_is_unreachable_not_an_error()
    {
        ollama.Fails = true;

        (await Create().GetAsync(Ct)).OllamaReachable.ShouldBeFalse();
    }

    [Fact]
    public async Task A_hanging_ping_is_unreachable_after_two_seconds()
    {
        ollama.Hangs = true;

        var status = Create().GetAsync(Ct);
        await ollama.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        time.Advance(TimeSpan.FromMilliseconds(1999));
        status.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromMilliseconds(1));

        (await status.WaitAsync(TimeSpan.FromSeconds(10), Ct)).OllamaReachable.ShouldBeFalse();
    }

    [Fact]
    public async Task An_aborted_request_is_not_reported_as_unreachable()
    {
        ollama.Hangs = true;
        using var request = new CancellationTokenSource();

        var status = Create().GetAsync(request.Token);
        await ollama.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await request.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => status);
    }

    [Fact]
    public async Task Account_mismatch_comes_from_the_account_guard()
    {
        accountGuard.Result = new AccountCheck(AccountCheckStatus.Mismatch, "o***@example.com");

        (await Create().GetAsync(Ct)).AccountMismatch.ShouldBeTrue();
    }

    private SetupStatusService Create() => new(
        settings,
        tokens,
        accountGuard,
        ollama,
        new GoogleClientService(
            settings, new EphemeralDataProtectionProvider(), Options.Create(env), NullLogger<GoogleClientService>.Instance),
        Options.Create(new GmailOptions { UseFake = useFakeGmail }),
        time,
        NullLogger<SetupStatusService>.Instance);

    private sealed class StubAccountGuard : IAccountGuard
    {
        public AccountCheck Result { get; set; } = new(AccountCheckStatus.Ok);

        public Task<AccountCheck> CheckAsync(CancellationToken ct = default) => Task.FromResult(Result);
    }

    private sealed class StubCatalog : IOllamaCatalog
    {
        public bool Fails { get; set; }
        public bool Hangs { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<OllamaModelDto>> ListModelsAsync(string? baseUrl = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task<string> PingAsync(string? baseUrl = null, CancellationToken ct = default)
        {
            Started.TrySetResult();
            if (Hangs)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return Fails
                ? throw new OllamaUnreachableException("synthetic failure", new HttpRequestException())
                : "0.0.1-test";
        }
    }
}
