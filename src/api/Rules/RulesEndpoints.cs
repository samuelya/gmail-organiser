using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Rules;

public static class RulesEndpoints
{
    public static IEndpointRouteBuilder MapRulesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/rules").WithTags("Rules");
        group.MapGet("/filters", ListFiltersAsync);
        group.MapPost("/filters/sync", SyncFiltersAsync).RequireAccountMatch();
        return endpoints;
    }

    /// <summary>The filter snapshot; empty with a null <c>syncedAt</c> before the first sync.</summary>
    private static async Task<Results<Ok<FilterListDto>, ProblemHttpResult>> ListFiltersAsync(
        FilterSnapshot snapshot, CancellationToken ct, bool includeDeleted = false)
    {
        try
        {
            return TypedResults.Ok(await snapshot.ListAsync(includeDeleted, ct));
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailNotConnected(ex);
        }
    }

    /// <summary>Reads the account's filters from Gmail into the snapshot; 503 when Gmail is not connected.</summary>
    private static async Task<Results<Ok<FilterSyncResultDto>, ProblemHttpResult>> SyncFiltersAsync(
        FilterSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await snapshot.SyncAsync(ct));
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailNotConnected(ex);
        }
    }

    private static ProblemHttpResult GmailNotConnected(GmailNotConnectedException ex) =>
        TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Gmail not connected", detail: ex.Message);
}
