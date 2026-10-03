using System.Text.Json;
using GmailOrganiser.Common;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Mcp;

/// <param name="ClaudeDesktopSnippet">The <c>claude_desktop_config.json</c> entry, bridged by <c>mcp-remote</c>.</param>
public sealed record McpConfigDto(string EndpointUrl, string Token, string ClaudeDesktopSnippet);

/// <summary>What a client needs to connect to <c>/mcp</c>, token rotation and the prompt to paste into Claude Desktop.</summary>
public static class McpConfigEndpoints
{
    private static readonly JsonSerializerOptions SnippetJson = new() { WriteIndented = true };

    public static IEndpointRouteBuilder MapMcpConfigEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/claude").WithTags("Claude");
        group.MapGet("/mcp-config", async (McpTokenService tokens, IOptions<AppOptions> app, CancellationToken ct) =>
            TypedResults.Ok(ToDto(app.Value, await tokens.GetOrCreateAsync(ct))));
        group.MapPost("/mcp-token/rotate", async (McpTokenService tokens, IOptions<AppOptions> app, CancellationToken ct) =>
            TypedResults.Ok(ToDto(app.Value, await tokens.RotateAsync(ct))));
        group.MapGet($"/prompts/{ReviewPrompts.ReviewPendingName}", () =>
            TypedResults.Text(ReviewPrompts.Render(ReviewItemBuilder.DefaultListLimit), "text/plain; charset=utf-8"));
        return endpoints;
    }

    internal static McpConfigDto ToDto(AppOptions app, string token)
    {
        var endpointUrl = app.NormalisedBaseUrl + McpExtensions.Path;
        var snippet = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                [McpExtensions.ServerName] = new
                {
                    command = "npx",
                    args = new[] { "mcp-remote", endpointUrl, "--header", $"Authorization: Bearer {token}" },
                },
            },
        };
        return new McpConfigDto(endpointUrl, token, JsonSerializer.Serialize(snippet, SnippetJson));
    }
}
