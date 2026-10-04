using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using Google;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Rules.Labels;

public static class LabelPlanEndpoints
{
    public static IEndpointRouteBuilder MapLabelPlanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/rules/labels").WithTags("Rules");
        group.MapPost("/plans", CreateAsync).RequireAccountMatch();
        group.MapGet("/plans/latest", LatestAsync);
        group.MapGet("/plans/{id:guid}", GetAsync);
        group.MapPatch("/plans/{id:guid}/items/{itemId:guid}", UpdateItemAsync);
        group.MapPost("/plans/{id:guid}/discard", DiscardAsync);
        group.MapPost("/plans/{id:guid}/apply", ApplyAsync).RequireAccountMatch();
        return endpoints;
    }

    /// <summary>
    /// Builds a new draft plan (the previous draft is discarded); 503 when Gmail is not connected or rate-limiting, 502
    /// when Gmail refuses a read.
    /// </summary>
    private static async Task<Results<Created<LabelPlanDto>, ProblemHttpResult>> CreateAsync(LabelPlanService plans, CancellationToken ct)
    {
        try
        {
            var plan = await plans.CreateAsync(ct);
            return TypedResults.Created($"/api/rules/labels/plans/{plan.Id}", plan);
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailProblems.NotConnected(ex);
        }
        catch (GmailRateLimitedException ex)
        {
            return GmailProblems.RateLimited(ex);
        }
        catch (GoogleApiException ex)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status502BadGateway, title: "Gmail refused a label read", detail: ex.Message);
        }
    }

    /// <summary>The newest plan that is not discarded; 404 when there is none.</summary>
    private static async Task<Results<Ok<LabelPlanDto>, NotFound>> LatestAsync(LabelPlanService plans, CancellationToken ct) =>
        await plans.LatestAsync(ct) is { } plan ? TypedResults.Ok(plan) : TypedResults.NotFound();

    private static async Task<Results<Ok<LabelPlanDto>, NotFound>> GetAsync(Guid id, LabelPlanService plans, CancellationToken ct) =>
        await plans.GetAsync(id, ct) is { } plan ? TypedResults.Ok(plan) : TypedResults.NotFound();

    /// <summary>Accepts, rejects or edits one item of a draft plan; 409 when the plan is not a draft.</summary>
    private static async Task<Results<Ok<LabelPlanDto>, NotFound, ProblemHttpResult>> UpdateItemAsync(
        Guid id, Guid itemId, UpdatePlanItemRequest request, LabelPlanService plans, CancellationToken ct)
    {
        if (request.Status is { } status && LabelPlanService.ParseStatus(status) is null)
        {
            return Invalid("The status must be accepted or rejected.");
        }

        if (request.ProposedName is { } name && (!LabelPath.IsValid(name) || LabelPath.IsReserved(name)))
        {
            return Invalid("The proposed name is not a valid label path.");
        }

        if (request.TargetLabelId is { } target && string.IsNullOrWhiteSpace(target))
        {
            return Invalid("The target label id is empty.");
        }

        try
        {
            return ToResult(await plans.UpdateItemAsync(id, itemId, request, ct));
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailProblems.NotConnected(ex);
        }
        catch (GmailRateLimitedException ex)
        {
            return GmailProblems.RateLimited(ex);
        }
    }

    /// <summary>Discards the plan; 409 once it is being applied.</summary>
    private static async Task<Results<Ok<LabelPlanDto>, NotFound, ProblemHttpResult>> DiscardAsync(
        Guid id, LabelPlanService plans, CancellationToken ct) =>
        ToResult(await plans.DiscardAsync(id, ct));

    /// <summary>Starts applying the plan's accepted items: 202 with the job id; 409 unless it is a draft with an accepted item.</summary>
    private static async Task<Results<Accepted<LabelPlanApplyDto>, NotFound, ProblemHttpResult>> ApplyAsync(
        Guid id, LabelPlanService plans, CancellationToken ct)
    {
        var result = await plans.ApplyAsync(id, ct);
        return result.Outcome switch
        {
            PlanEditOutcome.Ok => TypedResults.Accepted($"/api/jobs/{result.Plan!.JobId}", new LabelPlanApplyDto(result.Plan.JobId!.Value)),
            PlanEditOutcome.NotFound => TypedResults.NotFound(),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "The plan cannot be applied", detail: result.Detail),
        };
    }

    private static Results<Ok<LabelPlanDto>, NotFound, ProblemHttpResult> ToResult(PlanEditResult result) => result.Outcome switch
    {
        PlanEditOutcome.Ok => TypedResults.Ok(result.Plan!),
        PlanEditOutcome.NotFound => TypedResults.NotFound(),
        PlanEditOutcome.Conflict => TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict, title: "The plan cannot be changed", detail: result.Detail),
        _ => Invalid(result.Detail),
    };

    private static ProblemHttpResult Invalid(string? detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid plan item edit", detail: detail);
}
