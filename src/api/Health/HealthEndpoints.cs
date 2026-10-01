using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GmailOrganiser.Health;

public static class HealthEndpoints
{
    public static IServiceCollection AddHealthEndpoints(this IServiceCollection services)
    {
        services.AddHealthChecks();
        return services;
    }

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/healthz", new HealthCheckOptions { ResponseWriter = WriteResponse });
        return endpoints;
    }

    private static Task WriteResponse(HttpContext context, HealthReport report)
    {
        var status = report.Status == HealthStatus.Healthy ? "ok" : report.Status.ToString().ToLowerInvariant();
        return context.Response.WriteAsJsonAsync(new HealthDto(status));
    }
}

public sealed record HealthDto(string Status);
