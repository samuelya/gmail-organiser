using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
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

    /// <summary>The filter snapshot; empty with a null <c>syncedAt</c> before the first sync. Served while Gmail is unreachable.</summary>
    private static async Task<Ok<FilterListDto>> ListFiltersAsync(FilterSnapshot snapshot, CancellationToken ct, bool includeDeleted = false) =>
        TypedResults.Ok(await snapshot.ListAsync(includeDeleted, ct));

    /// <summary>Reads the account's filters from Gmail into the snapshot; 503 when Gmail is not connected or rate-limiting.</summary>
    private static async Task<Results<Ok<FilterSyncResultDto>, ProblemHttpResult>> SyncFiltersAsync(
        FilterSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await snapshot.SyncAsync(ct));
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailProblems.NotConnected(ex);
        }
        catch (GmailRateLimitedException ex)
        {
            return GmailProblems.RateLimited(ex);
        }
        catch (JobRefusedException)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict, type: AccountGuard.ProblemType, title: AccountGuard.ProblemTitle,
                detail: AccountGuard.ProblemDetail);
        }
    }
}
