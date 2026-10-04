using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Claude;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Review;

/// <summary>Accept or discard compare-run alternatives (#249). No Gmail calls.</summary>
public static class AlternativeEndpoints
{
    public static RouteGroupBuilder MapAlternativeEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/alternatives/accept", (AlternativeDecisionRequest request, AlternativeService alternatives, CancellationToken ct) =>
            DecideAsync(request, alternatives.AcceptAsync, ct));
        group.MapPost("/alternatives/discard", (AlternativeDecisionRequest request, AlternativeService alternatives, CancellationToken ct) =>
            DecideAsync(request, alternatives.DiscardAsync, ct));
        return group;
    }

    /// <summary>200 with the counts; 400 when neither ids nor groups are given, a group is invalid or a list is over the limit.</summary>
    private static async Task<Results<Ok<AlternativeDecisionResponse>, ValidationProblem>> DecideAsync(
        AlternativeDecisionRequest request,
        Func<Guid[], GroupRef[], CancellationToken, Task<AlternativeDecisionResponse>> decide,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.SuggestionIds is not { Length: > 0 } && request.Groups is not { Length: > 0 })
        {
            errors["request"] = ["Pass suggestionIds or groups."];
        }

        if (request.SuggestionIds is { Length: > AlternativeService.MaxTargets })
        {
            errors["suggestionIds"] = [$"At most {AlternativeService.MaxTargets} ids."];
        }

        var groups = GroupRefs.Normalise(request.Groups, AlternativeService.MaxTargets, errors, allowApplied: true);
        return errors.Count > 0
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await decide(request.SuggestionIds ?? [], groups, ct));
    }
}
