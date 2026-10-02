using Microsoft.Extensions.Options;

namespace GmailOrganiser.Common;

/// <summary>
/// Anti-CSRF guard for state-changing requests under <c>/api</c>. The app has no login and
/// registers no CORS policy, so a browser on another site can neither read responses nor send
/// the custom <c>X-Requested-With</c> header without a (refused) preflight. Every request under
/// <c>/hubs</c> (SignalR negotiate and the WebSocket upgrade, which no preflight protects) must
/// come from an allowed <c>Origin</c> when one is sent.
/// </summary>
public sealed class ApiRequestGuardMiddleware(RequestDelegate next, IOptions<SecurityOptions> options)
{
    public const string RequestedWithHeader = "X-Requested-With";

    private static readonly PathString ApiPrefix = new("/api");
    private static readonly PathString HubsPrefix = new("/hubs");

    // Normalised once; start-up validation guarantees every configured value normalises.
    private readonly HashSet<string> _allowedOrigins = new(
        options.Value.AllowedOrigins
            .Select(v => SecurityOptions.TryNormaliseOrigin(v, out var origin) ? origin : null)
            .OfType<string>(),
        StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetails)
    {
        var request = context.Request;
        var failure =
            request.Path.StartsWithSegments(HubsPrefix, StringComparison.OrdinalIgnoreCase) ? CheckOrigin(request, _allowedOrigins)
            : request.Path.StartsWithSegments(ApiPrefix, StringComparison.OrdinalIgnoreCase) && IsStateChanging(request.Method) ? Check(request, _allowedOrigins)
            : null;
        if (failure is null)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        // TryWriteAsync: when no writer accepts the request (e.g. Accept: text/plain) the 403
        // status set above is sent with an empty body instead of throwing into a 500.
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = failure,
            },
        });
    }

    internal static bool IsStateChanging(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    /// <returns>A reason when the request must be rejected, otherwise <c>null</c>.</returns>
    internal static string? Check(HttpRequest request, IReadOnlySet<string> allowedOrigins)
    {
        if (string.IsNullOrWhiteSpace(request.Headers[RequestedWithHeader].ToString()))
        {
            return $"The {RequestedWithHeader} header is required.";
        }

        return CheckOrigin(request, allowedOrigins);
    }

    /// <returns>A reason when an <c>Origin</c> is sent and is not allowed, otherwise <c>null</c>.</returns>
    internal static string? CheckOrigin(HttpRequest request, IReadOnlySet<string> allowedOrigins)
    {
        var origins = request.Headers.Origin;
        if (origins.Count == 0)
        {
            return null;
        }

        if (origins.Count > 1 || !allowedOrigins.Contains(origins.ToString().Trim()))
        {
            return "The request origin is not allowed.";
        }

        return null;
    }
}
