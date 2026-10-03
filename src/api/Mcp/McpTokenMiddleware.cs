using GmailOrganiser.Common;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace GmailOrganiser.Mcp;

/// <summary>
/// Guards <c>/mcp</c>: an <c>Origin</c>, when sent, must be in <c>Security:AllowedOrigins</c> (403), and every request
/// needs <c>Authorization: Bearer &lt;token&gt;</c> with the current <see cref="McpTokenService"/> token (401). No
/// <c>X-Requested-With</c>: MCP clients are not browsers.
/// </summary>
public sealed class McpTokenMiddleware(RequestDelegate next, IOptions<SecurityOptions> options)
{
    private const string BearerPrefix = "Bearer ";

    private readonly HashSet<string> _allowedOrigins = new(
        options.Value.AllowedOrigins
            .Select(v => SecurityOptions.TryNormaliseOrigin(v, out var origin) ? origin : null)
            .OfType<string>(),
        StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context, McpTokenService tokens, IProblemDetailsService problemDetails)
    {
        if (ApiRequestGuardMiddleware.CheckOrigin(context.Request, _allowedOrigins) is { } failure)
        {
            await RejectAsync(context, problemDetails, StatusCodes.Status403Forbidden, "Forbidden", failure);
            return;
        }

        if (!await tokens.IsValidAsync(BearerToken(context.Request), context.RequestAborted))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await RejectAsync(context, problemDetails, StatusCodes.Status401Unauthorized, "Unauthorized",
                "A valid Authorization: Bearer token is required; copy it from Settings.");
            return;
        }

        await next(context);
    }

    private static string? BearerToken(HttpRequest request)
    {
        var values = request.Headers[HeaderNames.Authorization];
        if (values.Count != 1 || values[0] is not { } value || !value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value[BearerPrefix.Length..].Trim();
    }

    private static async Task RejectAsync(HttpContext context, IProblemDetailsService problemDetails, int status, string title, string detail)
    {
        context.Response.StatusCode = status;
        // TryWriteAsync: when no writer accepts the request the status is sent with an empty body instead of a 500.
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        });
    }
}
