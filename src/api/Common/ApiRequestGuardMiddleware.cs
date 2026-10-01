using Microsoft.Extensions.Options;

namespace GmailOrganiser.Common;

/// <summary>
/// Anti-CSRF guard for state-changing requests under <c>/api</c>. The app has no login and
/// registers no CORS policy, so a browser on another site can neither read responses nor send
/// the custom <c>X-Requested-With</c> header without a (refused) preflight.
/// </summary>
public sealed class ApiRequestGuardMiddleware(RequestDelegate next, IOptions<SecurityOptions> options)
{
    public const string RequestedWithHeader = "X-Requested-With";

    private static readonly PathString ApiPrefix = new("/api");

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetails)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments(ApiPrefix, StringComparison.OrdinalIgnoreCase) || !IsStateChanging(request.Method))
        {
            await next(context);
            return;
        }

        var failure = Check(request, options.Value.AllowedOrigins);
        if (failure is null)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await problemDetails.WriteAsync(new ProblemDetailsContext
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
    internal static string? Check(HttpRequest request, IReadOnlyCollection<string> allowedOrigins)
    {
        if (string.IsNullOrWhiteSpace(request.Headers[RequestedWithHeader].ToString()))
        {
            return $"The {RequestedWithHeader} header is required.";
        }

        var origins = request.Headers.Origin;
        if (origins.Count == 0)
        {
            return null;
        }

        if (origins.Count > 1 || !IsAllowedOrigin(origins.ToString(), allowedOrigins))
        {
            return "The request origin is not allowed.";
        }

        return null;
    }

    private static bool IsAllowedOrigin(string origin, IReadOnlyCollection<string> allowedOrigins)
    {
        var normalised = origin.Trim().TrimEnd('/');
        return allowedOrigins.Any(allowed =>
            string.Equals(allowed.Trim().TrimEnd('/'), normalised, StringComparison.OrdinalIgnoreCase));
    }
}
