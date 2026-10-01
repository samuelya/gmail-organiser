using System.Net;
using System.Text;
using System.Text.Json;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>
/// Stub HTTP handler answering by request path (e.g. <c>/api/tags</c>); unknown paths return 404.
/// Records request URIs and bodies.
/// </summary>
public sealed class StubOllamaHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, string?, Task<HttpResponseMessage>>> _routes =
        new(StringComparer.Ordinal);
    private readonly List<(Uri Uri, string? Body)> _requests = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<(Uri Uri, string? Body)> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Throws from every request instead of routing, e.g. a connection failure.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Makes every request wait until cancelled (the HttpClient timeout fires).</summary>
    public bool Hang { get; set; }

    public StubOllamaHandler Json(string path, object body, HttpStatusCode status = HttpStatusCode.OK) =>
        Route(path, (_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        }));

    public StubOllamaHandler Route(string path, Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> handler)
    {
        _routes[path] = handler;
        return this;
    }

    /// <summary>A server with <c>/api/version</c>, <c>/api/tags</c> and one <c>/api/show</c> answer per model.</summary>
    public static StubOllamaHandler WithModels(params (string Name, string[]? Capabilities)[] models)
    {
        var stub = new StubOllamaHandler()
            .Json("/api/version", new { version = "0.0.1-test" })
            .Json("/api/tags", new
            {
                models = models.Select(m => new
                {
                    name = m.Name,
                    model = m.Name,
                    size = 1_000_000L,
                    digest = "sha256:0",
                    details = new { family = "testfamily", parameter_size = "1B" },
                }),
            });
        return stub.Route("/api/show", async (_, body) =>
        {
            var root = JsonDocument.Parse(body ?? "{}").RootElement;
            var name = (root.TryGetProperty("model", out var m) ? m : root.GetProperty("name")).GetString();
            var match = models.FirstOrDefault(m => m.Name == name);
            await Task.Yield();
            if (match.Name is null || match.Capabilities is null)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { capabilities = match.Capabilities }), Encoding.UTF8, "application/json"),
            };
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate)
        {
            _requests.Add((request.RequestUri!, body));
        }

        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        return _routes.TryGetValue(request.RequestUri!.AbsolutePath, out var handler)
            ? await handler(request, body)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> whose clients all use one handler (which is never disposed by them).</summary>
public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public List<string> Names { get; } = [];

    public HttpClient CreateClient(string name)
    {
        Names.Add(name);
        return new HttpClient(handler, disposeHandler: false);
    }
}
