using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Setup;

public static class SetupEndpoints
{
    public static IServiceCollection AddSetup(this IServiceCollection services) =>
        services.AddScoped<SetupStatusService>();

    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/setup").WithTags("Setup");
        group.MapGet("/status", StatusAsync);
        return endpoints;
    }

    private static async Task<Ok<SetupStatusDto>> StatusAsync(SetupStatusService setup, CancellationToken ct) =>
        TypedResults.Ok(await setup.GetAsync(ct));
}
