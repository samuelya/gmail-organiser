using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Llm.Fake;

/// <summary>
/// Answers with canned JSON (queued responses, then <see cref="Responder"/>, then <see cref="DefaultResponse"/>) and
/// records every request. Set <see cref="Failure"/> to make calls throw. Used by tests and by <c>LLM_FAKE=true</c>.
/// </summary>
public sealed class FakeChatClient(params string[] responses) : IChatClient
{
    private readonly Queue<string> _responses = new(responses);
    private readonly List<FakeChatRequest> _requests = [];
    private readonly Lock _gate = new();

    public string DefaultResponse { get; set; } = """{"ok":true}""";

    /// <summary>Computes an answer from the request when no response is queued; <c>null</c> falls back to <see cref="DefaultResponse"/>.</summary>
    public Func<IReadOnlyList<ChatMessage>, string?>? Responder { get; set; }
    public Exception? Failure { get; set; }
    public bool Disposed { get; private set; }

    public IReadOnlyList<FakeChatRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public void Enqueue(string response)
    {
        lock (_gate)
        {
            _responses.Enqueue(response);
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = Next(messages, options);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = Next(messages, options);
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => Disposed = true;

    private string Next(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        lock (_gate)
        {
            var request = new FakeChatRequest([.. messages], options);
            _requests.Add(request);
            if (Failure is not null)
            {
                throw Failure;
            }

            return _responses.TryDequeue(out var text) ? text : Responder?.Invoke(request.Messages) ?? DefaultResponse;
        }
    }
}

public sealed record FakeChatRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);
