using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Fetch;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Review;

/// <summary>Apply approved suggestions to Gmail as one History batch.</summary>
public static class ApplyEndpoints
{
    public const int MaxSuggestionIds = 1000;

    public static RouteGroupBuilder MapApplyEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/apply", ApplyAsync).RequireAccountMatch();
        return group;
    }

    /// <summary>202 with the batch (its job id included); 409 when nothing matching is approved.</summary>
    private static async Task<Results<Accepted<ActionBatchDto>, ValidationProblem, ProblemHttpResult>> ApplyAsync(
        ApplyRequest request, ApplyService apply, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var sender = request.SenderAddress?.Trim().ToLowerInvariant();
        if (request.SenderAddress is not null
            && (sender!.Length == 0 || sender.Length > AnalysisPreviewEndpoint.MaxSenderAddressLength))
        {
            errors["senderAddress"] = [$"At most {AnalysisPreviewEndpoint.MaxSenderAddressLength} characters, not blank."];
        }

        if (request.SuggestionIds is { Length: 0 or > MaxSuggestionIds })
        {
            errors["suggestionIds"] = [$"1 to {MaxSuggestionIds} ids, or omitted for every approved suggestion."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var ids = request.SuggestionIds is { } given ? given.Distinct().ToArray() : null;
        return await apply.StartAsync(sender, ids, ct) is { } batch
            ? TypedResults.Accepted($"/api/jobs/{batch.JobId}", batch)
            : TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Nothing to apply",
                detail: "No matching suggestion is approved.");
    }
}
