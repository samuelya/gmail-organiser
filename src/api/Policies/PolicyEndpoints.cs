using GmailOrganiser.Common;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Policies;

public static class PolicyEndpoints
{
    public static IEndpointRouteBuilder MapPolicyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/policies").WithTags("Policies");
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPut("/{id:guid}", EditAsync);
        group.MapPost("/{id:guid}/approve", ApproveAsync);
        group.MapPost("/{id:guid}/apply", ApplyAsync);
        group.MapPost("/{id:guid}/reject", RejectAsync);
        group.MapPost("/{id:guid}/rules/{ruleId:guid}/approve", (Guid id, Guid ruleId, PolicyService service, PolicyQuery query, CancellationToken ct) =>
            DecideRuleAsync(id, ruleId, PolicyStatus.Approved, service, query, ct));
        group.MapPost("/{id:guid}/rules/{ruleId:guid}/reject", (Guid id, Guid ruleId, PolicyService service, PolicyQuery query, CancellationToken ct) =>
            DecideRuleAsync(id, ruleId, PolicyStatus.Rejected, service, query, ct));
        group.MapDelete("/{id:guid}", DeleteAsync);
        return endpoints;
    }

    /// <summary>Policies of one status (default <c>proposed</c>), most mail first.</summary>
    private static async Task<Results<Ok<PagedDto<SenderPolicyDto>>, ValidationProblem>> ListAsync(
        PolicyQuery policies,
        CancellationToken ct,
        string? status = null,
        string? search = null,
        int? page = null,
        int? pageSize = null)
    {
        var query = PolicyQuery.Parse(status, search, page, pageSize, out var errors);
        return query is null ? TypedResults.ValidationProblem(errors) : TypedResults.Ok(await policies.ListAsync(query, ct));
    }

    /// <summary>The policy, its rules and a match preview over the scope's newest messages.</summary>
    private static async Task<Results<Ok<SenderPolicyDetailDto>, ProblemHttpResult>> GetAsync(Guid id, PolicyQuery policies, CancellationToken ct) =>
        await policies.GetAsync(id, ct) is { } detail ? TypedResults.Ok(detail) : NotFound();

    /// <summary>Replaces the policy and its rule list; 400 with field errors, 409 for a rejected policy.</summary>
    private static async Task<Results<Ok<EditPolicyResponse>, ValidationProblem, ProblemHttpResult>> EditAsync(
        Guid id, EditPolicyRequest request, PolicyService service, PolicyQuery policies, CancellationToken ct)
    {
        var (change, errors, reapply) = await service.EditAsync(id, request, ct);
        return change switch
        {
            PolicyChange.Done => TypedResults.Ok(new EditPolicyResponse(await DetailAsync(policies, id, ct), reapply)),
            PolicyChange.Invalid => TypedResults.ValidationProblem(errors),
            _ => Problem(change, "Only proposed and approved policies can be edited."),
        };
    }

    /// <summary>Approves a proposed policy and its proposed rules and queues its apply job; 422 when an outcome has no topic label, 409 otherwise.</summary>
    private static async Task<Results<Ok<ApprovePolicyResponse>, ProblemHttpResult>> ApproveAsync(
        Guid id, PolicyService service, PolicyQuery policies, CancellationToken ct)
    {
        var (change, jobId, reason) = await service.ApproveAsync(id, ct);
        return change == PolicyChange.Done
            ? TypedResults.Ok(new ApprovePolicyResponse(await SummaryAsync(policies, id, ct), jobId))
            : Problem(change, "Only a proposed policy can be approved.", reason);
    }

    /// <summary>Re-applies an approved policy to its past mail; 422 when an outcome has no topic label, 409 when not approved or while its job is queued or running.</summary>
    private static async Task<Results<Ok<ApprovePolicyResponse>, ProblemHttpResult>> ApplyAsync(
        Guid id, PolicyService service, PolicyQuery policies, CancellationToken ct)
    {
        var (change, jobId, reason) = await service.ApplyAsync(id, ct);
        return change == PolicyChange.Done
            ? TypedResults.Ok(new ApprovePolicyResponse(await SummaryAsync(policies, id, ct), jobId))
            : Problem(change, "Only an approved policy can be applied, one run at a time.", reason);
    }

    /// <summary>Rejects a proposed policy; 409 otherwise.</summary>
    private static async Task<Results<Ok<SenderPolicyDto>, ProblemHttpResult>> RejectAsync(
        Guid id, PolicyService service, PolicyQuery policies, CancellationToken ct)
    {
        var change = await service.RejectAsync(id, ct);
        return change == PolicyChange.Done
            ? TypedResults.Ok(await SummaryAsync(policies, id, ct))
            : Problem(change, "Only a proposed policy can be rejected.");
    }

    /// <summary>Approves or rejects one rule; returns the policy detail with the new preview.</summary>
    private static async Task<Results<Ok<SenderPolicyDetailDto>, ProblemHttpResult>> DecideRuleAsync(
        Guid id, Guid ruleId, PolicyStatus status, PolicyService service, PolicyQuery policies, CancellationToken ct)
    {
        var change = await service.DecideRuleAsync(id, ruleId, status, ct);
        return change == PolicyChange.Done
            ? TypedResults.Ok(await DetailAsync(policies, id, ct))
            : Problem(change, "The rule already has this status, or its policy is rejected.");
    }

    /// <summary>Deletes a rejected policy so the sender can be proposed again; 409 otherwise.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, PolicyService service, CancellationToken ct)
    {
        var change = await service.DeleteAsync(id, ct);
        return change == PolicyChange.Done ? TypedResults.NoContent() : Problem(change, "Only a rejected policy can be deleted.");
    }

    private static async Task<SenderPolicyDetailDto> DetailAsync(PolicyQuery policies, Guid id, CancellationToken ct) =>
        await policies.GetAsync(id, ct) ?? throw new InvalidOperationException($"Policy {id} vanished after a change.");

    private static async Task<SenderPolicyDto> SummaryAsync(PolicyQuery policies, Guid id, CancellationToken ct) =>
        await policies.GetSummaryAsync(id, ct) ?? throw new InvalidOperationException($"Policy {id} vanished after a change.");

    private static ProblemHttpResult Problem(PolicyChange change, string conflict, string? reason = null) => change switch
    {
        PolicyChange.NotFound => NotFound(),
        PolicyChange.Unappliable => TypedResults.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity, title: "Policy cannot be applied", detail: reason),
        _ => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Invalid policy transition", detail: conflict),
    };

    private static ProblemHttpResult NotFound() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Policy not found");
}
