using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>
/// The fake MCP client: the SDK's <see cref="McpClient"/> over the test host's in-memory handler, sending the bearer
/// token as Claude Code and <c>mcp-remote</c> do. Later MCP issues reuse it.
/// </summary>
public static class McpTestClient
{
    /// <summary>The current token from <c>GET /api/claude/mcp-config</c> (generated on first use).</summary>
    public static async Task<McpConfigDto> GetConfigAsync(WebApplicationFactory<Program> host, CancellationToken ct) =>
        (await host.CreateClient().GetFromJsonAsync<McpConfigDto>("/api/claude/mcp-config", ct))!;

    /// <summary>Connects with <paramref name="token"/>, or the current token when null.</summary>
    public static async Task<McpClient> ConnectAsync(WebApplicationFactory<Program> host, CancellationToken ct, string? token = null)
    {
        token ??= (await GetConfigAsync(host, ct)).Token;
        var http = host.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, McpExtensions.Path),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
            },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    /// <summary>Calls <paramref name="tool"/> and returns its result; the arguments use the tools' snake_case names.</summary>
    public static Task<CallToolResult> CallAsync(McpClient client, string tool, CancellationToken ct, IReadOnlyDictionary<string, object?>? arguments = null) =>
        client.CallToolAsync(tool, arguments, cancellationToken: ct).AsTask();

    /// <summary>The structured content of a successful call.</summary>
    public static JsonElement Structured(CallToolResult result)
    {
        result.IsError.ShouldNotBe(true, string.Join('\n', result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        return result.StructuredContent.ShouldNotBeNull().Deserialize<JsonElement>();
    }

    /// <summary>The text of an <c>isError</c> result.</summary>
    public static string ErrorText(CallToolResult result)
    {
        result.IsError.ShouldBe(true);
        return string.Join('\n', result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}
