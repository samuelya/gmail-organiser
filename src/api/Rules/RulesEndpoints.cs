using System.Net;
using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Rules.Review;
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
        group.MapPost("/filters/reviews", CreateReviewAsync).RequireAccountMatch();
        group.MapGet("/filters/reviews/latest", LatestReviewAsync);
        group.MapGet("/filters/reviews/{id:guid}", GetReviewAsync);
        group.MapPost("/filters/reviews/{id:guid}/summary", SummariseReviewAsync);
        group.MapPost("/filters/findings/{id:guid}/apply", ApplyFindingAsync).RequireAccountMatch();
        group.MapPost("/filters/findings/{id:guid}/dismiss", DismissFindingAsync).RequireAccountMatch();
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

    /// <summary>
    /// Filters proposed from approved policies, then approved senders, that no active filter covers; <c>source</c> picks
    /// the kind and defaults to <c>pattern</c>, which the current web UI expects.
    /// </summary>
    private static async Task<Results<Ok<PagedDto<FilterProposalDto>>, ValidationProblem>> ListProposalsAsync(
        FilterProposalQuery proposals, CancellationToken ct, int? page = null, int? pageSize = null, string? source = null)
    {
        var errors = new Dictionary<string, string[]>();
        if (source is not null && !FilterProposalSources.IsValid(source))
        {
            errors["source"] = ["Must be 'policy', 'pattern' or 'all'."];
        }

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

        return TypedResults.Ok(await proposals.ListAsync(
            source ?? FilterProposalSources.Pattern, page ?? 1, pageSize ?? FilterProposalQuery.DefaultPageSize, ct));
    }

    /// <summary>Syncs the filters and stores a new review; 503 when Gmail is unreachable, 409 for another account's data.</summary>
    private static async Task<Results<Created<FilterReviewDto>, ProblemHttpResult>> CreateReviewAsync(
        FilterReviewService reviews, CancellationToken ct)
    {
        try
        {
            var review = await reviews.CreateAsync(ct);
            return TypedResults.Created($"/api/rules/filters/reviews/{review.Id}", review);
        }
        catch (Exception ex) when (GmailProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>The newest filter review; 404 when there is none.</summary>
    private static async Task<Results<Ok<FilterReviewDto>, NotFound>> LatestReviewAsync(FilterReviewService reviews, CancellationToken ct) =>
        await reviews.LatestAsync(ct) is { } review ? TypedResults.Ok(review) : TypedResults.NotFound();

    private static async Task<Results<Ok<FilterReviewDto>, NotFound>> GetReviewAsync(
        Guid id, FilterReviewService reviews, CancellationToken ct) =>
        await reviews.GetAsync(id, ct) is { } review ? TypedResults.Ok(review) : TypedResults.NotFound();

    /// <summary>
    /// Asks the chat model for a summary of the review's findings; a model failure is the same 200 with
    /// <c>summaryError</c> set. 409 when the review has no findings, 503 when no chat model is chosen.
    /// </summary>
    private static async Task<Results<Ok<FilterReviewDto>, NotFound, ProblemHttpResult>> SummariseReviewAsync(
        Guid id, FilterReviewSummariser summariser, CancellationToken ct) =>
        await summariser.SummariseAsync(id, ct) switch
        {
            { Outcome: SummaryOutcome.Ok, Review: { } review } => TypedResults.Ok(review),
            { Outcome: SummaryOutcome.NoFindings } => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "Nothing to summarise", detail: "The review has no findings."),
            { Outcome: SummaryOutcome.NotConfigured } => TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable, title: "LLM not configured",
                detail: "No chat model is selected. Choose one in Settings."),
            _ => TypedResults.NotFound(),
        };

    /// <summary>
    /// Runs the finding's fix; 409 when it is not open, a referenced filter is gone or Gmail refuses a step (the finding
    /// keeps the error and stays open), 503 when Gmail is unreachable.
    /// </summary>
    private static async Task<Results<Ok<FilterFindingDto>, NotFound, ProblemHttpResult>> ApplyFindingAsync(
        Guid id, FilterReviewService reviews, CancellationToken ct)
    {
        try
        {
            return ToResult(await reviews.ApplyAsync(id, ct));
        }
        catch (Exception ex) when (GmailProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>Dismisses an open finding; 409 otherwise.</summary>
    private static async Task<Results<Ok<FilterFindingDto>, NotFound, ProblemHttpResult>> DismissFindingAsync(
        Guid id, FilterReviewService reviews, CancellationToken ct) =>
        ToResult(await reviews.DismissAsync(id, ct));

    private static Results<Ok<FilterFindingDto>, NotFound, ProblemHttpResult> ToResult(FindingResult result) => result switch
    {
        { Outcome: FilterOutcome.Ok, Finding: { } finding } => TypedResults.Ok(finding),
        { Outcome: FilterOutcome.NotFound } => TypedResults.NotFound(),
        _ => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Title, detail: result.Detail),
    };

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
