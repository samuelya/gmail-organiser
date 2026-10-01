using System.Net.Sockets;
using GmailOrganiser.Llm;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Unit;

public sealed class LlmModelTesterTests
{
    private static readonly Uri BaseUrl = new("http://ollama.example.com:11434");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeChatClient _chat = new();
    private readonly FakeEmbeddingGenerator _embed = new(dimension: 4);

    private LlmModelTester Tester() => new(
        new FakeLlmClientFactory(_chat, _embed),
        Options.Create(new LlmOptions()),
        new FakeTimeProvider(),
        NullLogger<LlmModelTester>.Instance);

    [Fact]
    public async Task Chat_success_sends_one_json_mode_request()
    {
        var result = await Tester().TestAsync(ModelKinds.Chat, "test-chat:1b", BaseUrl, Ct);

        result.Ok.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.ElapsedMs.ShouldBeGreaterThanOrEqualTo(0);
        var request = _chat.Requests.ShouldHaveSingleItem();
        request.Options!.ResponseFormat.ShouldBe(ChatResponseFormat.Json);
        request.Messages.Single().Text.ShouldContain("""{"ok":true}""");
        _chat.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Chat_reply_that_is_not_json_fails()
    {
        _chat.Enqueue("sure, here you go");

        var result = await Tester().TestAsync(ModelKinds.Chat, "test-chat:1b", BaseUrl, Ct);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Chat_connection_failure_is_a_readable_error()
    {
        _chat.Failure = new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused));

        var result = await Tester().TestAsync(ModelKinds.Chat, "test-chat:1b", BaseUrl, Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("Connection refused");
    }

    [Fact]
    public async Task Missing_model_keeps_the_server_message()
    {
        _chat.Failure = new HttpRequestException("model 'test-missing' not found", null, System.Net.HttpStatusCode.NotFound);

        var result = await Tester().TestAsync(ModelKinds.Chat, "test-missing", BaseUrl, Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("not found");
    }

    [Fact]
    public async Task Embedding_success_embeds_one_word()
    {
        var result = await Tester().TestAsync(ModelKinds.Embedding, "test-embed", BaseUrl, Ct);

        result.Ok.ShouldBeTrue();
        _embed.Inputs.ShouldBe([LlmModelTester.EmbeddingInput]);
        _embed.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Embedding_failure_is_reported()
    {
        _embed.Failure = new TaskCanceledException("timeout", new TimeoutException());

        var result = await Tester().TestAsync(ModelKinds.Embedding, "test-embed", BaseUrl, Ct);

        result.Ok.ShouldBeFalse();
        result.Error!.ShouldContain("No answer");
    }

    [Fact]
    public void Fake_embeddings_are_deterministic_unit_vectors()
    {
        var a = _embed.Vector("hello");

        a.ShouldBe(new FakeEmbeddingGenerator(4).Vector("hello"));
        a.ShouldNotBe(_embed.Vector("world"));
        MathF.Sqrt(a.Sum(x => x * x)).ShouldBe(1f, 1e-5f);
        new FakeEmbeddingGenerator(100).Vector("hello").Length.ShouldBe(100);
    }
}
