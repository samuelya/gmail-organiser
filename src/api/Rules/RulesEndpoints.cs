using System.Net;
using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using Google;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Rules;

public static class RulesEndpoints
{
    public static IEndpointRouteBuilder MapRulesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/rules").WithTags("Rules");
        group.MapGet("/filters", ListFiltersAsync);
        group.MapPost("/filters/sync", SyncFiltersAsync).RequireAccountMatch();
        group.MapPost("/filters/preview", PreviewFilterAsync);
        group.MapPost("/filters", CreateFilterAsync).RequireAccountMatch();
        group.MapDelete("/filters/{id}", DeleteFilterAsync).RequireAccountMatch();
        group.MapPost("/filters/{id}/restore", RestoreFilterAsync).RequireAccountMatch();
        group.MapGet("/filters/proposals", ListProposalsAsync);
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
        catch (Exception ex) when (GmailProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>The criteria's Gmail query, local match count and Gmail estimate; Gmail problems are warnings, not errors.</summary>
    private static async Task<Results<Ok<FilterPreviewDto>, ValidationProblem>> PreviewFilterAsync(
        FilterPreviewRequest request, FilterService filters, CancellationToken ct)
    {
        if (FilterCriteriaMapping.TryRead(request.Criteria, request.Action, out var errors) is not { } spec)
        {
            return TypedResults.ValidationProblem(errors);
        }

        return TypedResults.Ok(await filters.PreviewAsync(spec, ct));
    }

    /// <summary>Creates the filter (and any missing label); 409 at Gmail's limits or on Gmail's refusal, 503 when Gmail is unreachable.</summary>
    private static async Task<Results<Created<FilterDto>, ValidationProblem, ProblemHttpResult>> CreateFilterAsync(
        CreateFilterRequest request, FilterService filters, CancellationToken ct)
    {
        if (FilterCriteriaMapping.TryRead(request.Criteria, request.Action, out var errors) is not { } spec)
        {
            return TypedResults.ValidationProblem(errors);
        }

        try
        {
            return Created(await filters.CreateAsync(spec, ct));
        }
        catch (Exception ex) when (GmailProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>Deletes the filter in Gmail and marks its row; 404 unknown, 409 already deleted.</summary>
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteFilterAsync(
        string id, FilterService filters, CancellationToken ct)
    {
        try
        {
            return await filters.DeleteAsync(id, ct) switch
            {
                { Outcome: FilterOutcome.Ok } => TypedResults.NoContent(),
                { Outcome: FilterOutcome.NotFound } => TypedResults.NotFound(),
                var conflict => ConflictProblem(conflict),
            };
        }
        catch (Exception ex) when (GmailProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>Re-creates a deleted filter as a new one; 404 when the row is not deleted, 409 when a label is gone.</summary>
    private static async Task<Results<Created<FilterDto>, NotFound, ProblemHttpResult>> RestoreFilterAsync(
        string id, FilterService filters, CancellationToken ct)
    {
        try
        {
            return await filters.RestoreAsync(id, ct) switch
            {
                { Outcome: FilterOutcome.NotFound } => TypedResults.NotFound(),
                { Outcome: FilterOutcome.Ok, Filter: { } filter } => CreatedAt(filter),
                var conflict => ConflictProblem(conflict),
            };
        }
        catch (Exception ex) when (GmailProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>Filters proposed from approved senders no active filter covers, most messages first.</summary>
    private static async Task<Results<Ok<PagedDto<FilterProposalDto>>, ValidationProblem>> ListProposalsAsync(
        FilterProposalQuery proposals, CancellationToken ct, int? page = null, int? pageSize = null)
    {
        var errors = new Dictionary<string, string[]>();
        if (page is < 1)
        {
            errors["page"] = ["Must be at least 1."];
        }

        if (pageSize is < 1 or > FilterProposalQuery.MaxPageSize)
        {
            errors["pageSize"] = [$"Must be between 1 and {FilterProposalQuery.MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        return TypedResults.Ok(await proposals.ListAsync(page ?? 1, pageSize ?? FilterProposalQuery.DefaultPageSize, ct));
    }

    private static Results<Created<FilterDto>, ValidationProblem, ProblemHttpResult> Created(FilterResult result) =>
        result is { Outcome: FilterOutcome.Ok, Filter: { } filter } ? CreatedAt(filter) : ConflictProblem(result);

    private static Created<FilterDto> CreatedAt(FilterDto filter) =>
        TypedResults.Created($"/api/rules/filters/{Uri.EscapeDataString(filter.Id)}", filter);

    private static ProblemHttpResult ConflictProblem(FilterResult result) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Title, detail: result.Detail);

    /// <summary>
    /// 503 when Gmail is not connected or rate-limiting; 409 when the local data belongs to another account or Gmail
    /// refused the request (400/409, e.g. "Filter already exists"), with Gmail's message.
    /// </summary>
    private static ProblemHttpResult? GmailProblem(Exception ex) => ex switch
    {
        GoogleApiException { HttpStatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Conflict } e => TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict, title: "Gmail refused the change", detail: e.Error?.Message ?? e.Message),
        GmailNotConnectedException e => GmailProblems.NotConnected(e),
        GmailRateLimitedException e => GmailProblems.RateLimited(e),
        JobRefusedException => TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict, type: AccountGuard.ProblemType, title: AccountGuard.ProblemTitle,
            detail: AccountGuard.ProblemDetail),
        _ => null,
    };
}
