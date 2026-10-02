using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Senders;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Review;

/// <summary>The History page: batches of Gmail changes, their log, and undo.</summary>
public static class HistoryEndpoints
{
    public static IEndpointRouteBuilder MapHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/history").WithTags("History");
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/{id:guid}/undo", UndoAsync).RequireAccountMatch();
        return endpoints;
    }

    /// <summary>Batches newest first.</summary>
    private static async Task<Results<Ok<PagedDto<ActionBatchDto>>, ValidationProblem>> ListAsync(
        HistoryQuery history, CancellationToken ct, int? page = null, int? pageSize = null)
    {
        var paging = SenderQuery.Parse(null, page, pageSize, null, null, out var errors);
        return paging is null
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await history.ListAsync(paging.Page, paging.PageSize, ct));
    }

    private static async Task<Results<Ok<ActionBatchDetailDto>, NotFound>> GetAsync(Guid id, HistoryQuery history, CancellationToken ct) =>
        await history.GetAsync(id, ct) is { } detail ? TypedResults.Ok(detail) : TypedResults.NotFound();

    /// <summary>202 with the undo batch (its job id included); 404 for an unknown batch; 409 when it cannot be undone now.</summary>
    private static async Task<Results<Accepted<ActionBatchDto>, NotFound, ProblemHttpResult>> UndoAsync(
        Guid id, HistoryQuery history, CancellationToken ct)
    {
        var start = await history.StartUndoAsync(id, ct);
        return start switch
        {
            { Batch: { } batch } => TypedResults.Accepted($"/api/jobs/{batch.JobId}", batch),
            { Refusal: { } reason } => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Cannot undo", detail: reason),
            _ => TypedResults.NotFound(),
        };
    }
}
