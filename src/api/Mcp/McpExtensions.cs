using System.Reflection;
using ModelContextProtocol.Protocol;

namespace GmailOrganiser.Mcp;

/// <summary>
/// The MCP server at <see cref="Path"/> (DESIGN §6.7): Streamable HTTP in stateless mode, so a dropped connection or a
/// restart loses nothing; guarded by <see cref="McpTokenMiddleware"/>. Review tools only.
/// </summary>
public static class McpExtensions
{
    public const string Path = "/mcp";
    public const string ServerName = "gmail-organiser";

    public static IServiceCollection AddMcpServer(this IServiceCollection services)
    {
        services.AddScoped<McpTokenService>();
        services.AddScoped<LabelTreeBuilder>();
        services.AddScoped<ReviewItemBuilder>();
        // The SDK logs JSON-RPC traffic below Warning; that traffic carries email content.
        services.AddLogging(b => b.AddFilter("ModelContextProtocol", LogLevel.Warning));
        services.AddMcpServer(o => o.ServerInfo = new Implementation
        {
            Name = ServerName,
            Version = typeof(McpExtensions).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<ReviewTools>();
        return services;
    }

    /// <summary>Applies <see cref="McpTokenMiddleware"/> to <see cref="Path"/> and maps the server and its config endpoints.</summary>
    public static WebApplication MapMcpServer(this WebApplication app)
    {
        app.UseWhen(
            c => c.Request.Path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase),
            branch => branch.UseMiddleware<McpTokenMiddleware>());
        app.MapMcp(Path);
        app.MapMcpConfigEndpoints();
        return app;
    }
}
