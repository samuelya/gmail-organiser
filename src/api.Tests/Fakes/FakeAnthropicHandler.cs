using System.Net;
using System.Text;
using System.Text.Json;
using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>
/// Stands in for the Anthropic Messages API: answers each request with the next queued response (the last one repeats)
/// and records the request host, path, query, headers and JSON body. Synthetic content only.
/// </summary>
public sealed class FakeAnthropicHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();
    private readonly List<Recorded> _requests = [];
    private readonly Lock _gate = new();
    private Func<HttpResponseMessage>? _last;

    public sealed record Recorded(string Host, string Path, IReadOnlyDictionary<string, string> Headers, JsonElement Body, string Query = "");

    public IReadOnlyList<Recorded> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public bool Disposed { get; private set; }

    /// <summary>A successful <c>/v1/messages</c> answer with one text block and the given usage.</summary>
    public FakeAnthropicHandler Message(string text, int inputTokens = 11, int outputTokens = 7) =>
        Enqueue(HttpStatusCode.OK, new
        {
            id = "msg_test_1",
            type = "message",
            role = "assistant",
            model = "test-model-a",
            content = new[] { new { type = "text", text } },
            stop_reason = "end_turn",
            stop_sequence = (string?)null,
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
        });

    /// <summary>One <c>/v1/models</c> page; <c>last_id</c> is the last model's id.</summary>
    public FakeAnthropicHandler Models(bool hasMore, params (string Id, string DisplayName, string? CreatedAt)[] models) =>
        Enqueue(HttpStatusCode.OK, new
        {
            data = models.Select(m => new { type = "model", id = m.Id, display_name = m.DisplayName, created_at = m.CreatedAt }),
            has_more = hasMore,
            first_id = models.FirstOrDefault().Id,
            last_id = models.LastOrDefault().Id,
        });

    /// <summary>Throws <paramref name="exception"/> instead of answering, as a refused connection would.</summary>
    public FakeAnthropicHandler Throw(Exception exception)
    {
        _responses.Enqueue(() => throw exception);
        return this;
    }

    /// <summary>A 200 answer whose body is not a message, as an API change or a broken proxy would send.</summary>
    public FakeAnthropicHandler Unreadable() => Enqueue(HttpStatusCode.OK, new { type = "message", content = "synthetic" });

    /// <summary>An Anthropic error body with <paramref name="status"/>, e.g. 429 or 529, plus headers such as <c>retry-after</c>.</summary>
    public FakeAnthropicHandler Error(HttpStatusCode status, string type, params (string Name, string Value)[] headers) =>
        Enqueue(status, new { type = "error", error = new { type, message = "synthetic error" } }, headers);

    private FakeAnthropicHandler Enqueue(HttpStatusCode status, object body, params (string Name, string Value)[] headers)
    {
        var json = JsonSerializer.Serialize(body);
        _responses.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        lock (_gate)
        {
            _requests.Add(new Recorded(request.RequestUri!.Host, request.RequestUri.AbsolutePath, headers, JsonDocument.Parse(body).RootElement.Clone(), request.RequestUri.Query));
            if (_responses.TryDequeue(out var next))
            {
                _last = next;
            }
        }

        return (_last ?? throw new InvalidOperationException("No response queued.")).Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// A <see cref="ClaudeApiKeyService"/> over a settings store with throwaway Data Protection keys, shared by the test run
/// so a key one service saves, another reads.
/// </summary>
public static class TestClaudeApiKeys
{
    private static readonly EphemeralDataProtectionProvider DataProtection = new();

    public static ClaudeApiKeyService For(ISettingsStore settings) =>
        new(settings, DataProtection, NullLogger<ClaudeApiKeyService>.Instance);
}
