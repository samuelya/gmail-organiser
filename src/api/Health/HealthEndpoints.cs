using System.Reflection;
using GmailOrganiser.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GmailOrganiser.Health;

public static class HealthEndpoints
{
    /// <summary>The api's informational version (Directory.Build.props, or the image's VERSION build arg) without the
    /// "+&lt;sha&gt;" source-revision suffix the SDK appends.</summary>
    public static readonly string Version = ReadVersion();

    public static IServiceCollection AddHealthEndpoints(this IServiceCollection services)
    {
        // Unhealthy maps to 503 (default HealthCheckOptions.ResultStatusCodes).
        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");
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
        return context.Response.WriteAsJsonAsync(new HealthDto(status, Version));
    }

    private static string ReadVersion()
    {
        // The api assembly rather than GetEntryAssembly(): under WebApplicationFactory the entry assembly is the test host.
        var informational = typeof(HealthEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}

public sealed record HealthDto(string Status, string Version);
