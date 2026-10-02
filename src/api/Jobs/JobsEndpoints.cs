using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Jobs;

public static class JobsEndpoints
{
    public static IEndpointRouteBuilder MapJobsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/jobs").WithTags("Jobs");
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/{id:guid}/pause", (Guid id, IJobService jobs, CancellationToken ct) =>
            ToResultAsync(jobs.PauseAsync(id, ct), "pause"));
        group.MapPost("/{id:guid}/resume", (Guid id, IJobService jobs, CancellationToken ct) =>
            ToResultAsync(jobs.ResumeAsync(id, ct), "resume"));
        group.MapPost("/{id:guid}/cancel", (Guid id, IJobService jobs, CancellationToken ct) =>
            ToResultAsync(jobs.CancelAsync(id, ct), "cancel"));
        return endpoints;
    }

    /// <summary>Active jobs plus the most recently finished ones; <c>?activeOnly=true</c> for active only.</summary>
    private static async Task<Ok<IReadOnlyList<JobDto>>> ListAsync(IJobService jobs, CancellationToken ct, bool activeOnly = false) =>
        TypedResults.Ok(await jobs.ListAsync(activeOnly, ct));

    private static async Task<Results<Ok<JobDto>, ProblemHttpResult>> GetAsync(Guid id, IJobService jobs, CancellationToken ct) =>
        await jobs.GetAsync(id, ct) is { } job ? TypedResults.Ok(job) : NotFound();

    private static async Task<Results<NoContent, ProblemHttpResult>> ToResultAsync(Task<JobActionResult> action, string verb) =>
        await action switch
        {
            JobActionResult.Ok => TypedResults.NoContent(),
            JobActionResult.NotFound => NotFound(),
            JobActionResult.Refused => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Job has unfinished work",
                detail: $"The job cannot {verb} now: resume it to finish the step in progress."),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Job state does not allow this",
                detail: $"The job cannot {verb} in its current state."),
        };

    private static ProblemHttpResult NotFound() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found");
}
