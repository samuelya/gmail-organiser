using GmailOrganiser.Settings;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Rules;

/// <param name="Config">The <c>const CONFIG = {...};</c> block to paste into the script.</param>
public sealed record AppsScriptConfigDto(int ScriptVersion, string Config, DateTimeOffset GeneratedAt);

public static class AppsScriptEndpoints
{
    public static IEndpointRouteBuilder MapAppsScriptEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/api/rules/apps-script").WithTags("Rules").MapGet("/config", GetConfigAsync);
        return endpoints;
    }

    /// <summary>The script's CONFIG block from the saved settings; reads no Gmail, so missing labels are the script's concern.</summary>
    private static async Task<Ok<AppsScriptConfigDto>> GetConfigAsync(ISettingsStore store, TimeProvider time, CancellationToken ct)
    {
        var settings = await store.GetAsync(ct);
        return TypedResults.Ok(new AppsScriptConfigDto(
            AppsScriptConfigGenerator.ScriptVersion, AppsScriptConfigGenerator.Generate(settings), time.GetUtcNow()));
    }
}
