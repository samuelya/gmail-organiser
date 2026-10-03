using System.Net;
using System.Text;
using System.Text.Json;
using GmailOrganiser.Mcp;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The <c>/mcp</c> endpoint (#164): bearer token, Origin check, transport errors, config and token rotation.</summary>
[Collection(PostgresCollection.Name)]
public sealed class McpServerAuthTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Config_generates_one_token_and_the_claude_desktop_snippet()
    {
        var config = await McpTestClient.GetConfigAsync(factory, Ct);

        config.EndpointUrl.ShouldBe(ApiFactory.AllowedOrigin + "/mcp");
        config.Token.Length.ShouldBe(43);
        config.Token.ShouldMatch("^[A-Za-z0-9_-]+$");
        (await McpTestClient.GetConfigAsync(factory, Ct)).Token.ShouldBe(config.Token);

        using var snippet = JsonDocument.Parse(config.ClaudeDesktopSnippet);
        var server = snippet.RootElement.GetProperty("mcpServers").GetProperty("gmail-organiser");
        server.GetProperty("command").GetString().ShouldBe("npx");
        server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ShouldBe(
            ["mcp-remote", config.EndpointUrl, "--header", $"Authorization: Bearer {config.Token}"]);

        // Stored encrypted, and never part of the settings API.
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Settings.SingleAsync(Ct)).Document.ShouldNotContain(config.Token);
        }

        var settings = await factory.CreateClient().GetStringAsync("/api/settings", Ct);
        settings.ShouldNotContain(config.Token);
        settings.ShouldNotContain("mcpToken", Case.Insensitive);
    }

    [Fact]
    public async Task Client_with_the_token_lists_the_three_read_only_tools_without_x_requested_with()
    {
        await using var client = await McpTestClient.ConnectAsync(factory, Ct);

        client.ServerInfo.Name.ShouldBe(McpExtensions.ServerName);
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        tools.Select(t => t.Name).Order().ShouldBe(["get_label_tree", "get_review_item", "list_pending_reviews"]);
        foreach (var tool in tools)
        {
            tool.ProtocolTool.Annotations!.ReadOnlyHint.ShouldBe(true);
            tool.ProtocolTool.Annotations.DestructiveHint.ShouldBe(false);
            tool.Description.ShouldContain("untrusted email data");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong-token")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public async Task Missing_or_wrong_token_is_401_problem_details(string? authorization)
    {
        await McpTestClient.GetConfigAsync(factory, Ct);

        var response = await PostAsync(authorization);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task No_token_generated_yet_rejects_every_bearer()
    {
        (await PostAsync("Bearer anything")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Origin_must_be_allowed_when_sent()
    {
        var token = (await McpTestClient.GetConfigAsync(factory, Ct)).Token;

        var foreign = await PostAsync($"Bearer {token}", origin: "http://evil.example.com");
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        foreign.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        // A disallowed Origin is refused before the token is looked at.
        (await PostAsync(null, origin: "http://evil.example.com")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await PostAsync($"Bearer {token}", origin: ApiFactory.AllowedOrigin)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync($"Bearer {token}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Missing_accept_header_is_refused_by_the_transport()
    {
        var token = (await McpTestClient.GetConfigAsync(factory, Ct)).Token;

        var response = await PostAsync($"Bearer {token}", accept: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotAcceptable);
    }

    [Fact]
    public async Task Rotation_replaces_the_token_and_the_old_one_stops_working()
    {
        var old = (await McpTestClient.GetConfigAsync(factory, Ct)).Token;
        var http = factory.CreateClient();

        // State-changing /api call: the anti-CSRF header is required.
        (await http.PostAsync("/api/claude/mcp-token/rotate", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        var rotate = await http.PostAsync("/api/claude/mcp-token/rotate", null, Ct);
        rotate.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotated = JsonSerializer.Deserialize<McpConfigDto>(await rotate.Content.ReadAsStringAsync(Ct), JsonSerializerOptions.Web)!;

        rotated.Token.ShouldNotBe(old);
        rotated.ClaudeDesktopSnippet.ShouldContain(rotated.Token);
        (await McpTestClient.GetConfigAsync(factory, Ct)).Token.ShouldBe(rotated.Token);
        (await PostAsync($"Bearer {old}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await using var client = await McpTestClient.ConnectAsync(factory, Ct, rotated.Token);
        (await client.ListToolsAsync(cancellationToken: Ct)).Count.ShouldBe(3);
    }

    private async Task<HttpResponseMessage> PostAsync(string? authorization, string? origin = null, string? accept = "application/json, text/event-stream")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, McpExtensions.Path)
        {
            Content = new StringContent(Initialize, Encoding.UTF8, "application/json"),
        };
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        return await factory.CreateClient().SendAsync(request, Ct);
    }
}
