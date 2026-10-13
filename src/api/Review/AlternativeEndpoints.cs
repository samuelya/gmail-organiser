using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Claude;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Review;

/// <summary>Accept or discard compare-run alternatives (#249), or copy an outcome as alternatives (#562). No Gmail calls.</summary>
public static class AlternativeEndpoints
{
    public static RouteGroupBuilder MapAlternativeEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/alternatives/accept", (AlternativeDecisionRequest request, AlternativeService alternatives, CancellationToken ct) =>
            DecideAsync(request, alternatives.AcceptAsync, ct));
        group.MapPost("/alternatives/discard", (AlternativeDecisionRequest request, AlternativeService alternatives, CancellationToken ct) =>
            DecideAsync(request, alternatives.DiscardAsync, ct));
        group.MapPost("/alternatives/copy", CopyAsync);
        return group;
    }

    /// <summary>
    /// 200 with the counts; 400 without a source or targets, or over <see cref="SettingsValidation.MaxAnalysisDefaultCount"/>
    /// targets; 404 unknown source; 409 while an analysis run is queued or running.
    /// </summary>
    private static async Task<Results<Ok<AlternativeCopyResponse>, ValidationProblem, NotFound, ProblemHttpResult>> CopyAsync(
        AlternativeCopyRequest request, AlternativeCopyService copies, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.SourceSuggestionId is null)
        {
            errors["sourceSuggestionId"] = ["Required."];
        }

        if (request.SuggestionIds is not { Length: > 0 })
        {
            errors["suggestionIds"] = ["Pass at least one id."];
        }
        else if (request.SuggestionIds.Length > SettingsValidation.MaxAnalysisDefaultCount)
        {
            errors["suggestionIds"] = [$"At most {SettingsValidation.MaxAnalysisDefaultCount} ids."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var (result, response) = await copies.CopyAsync(request.SourceSuggestionId!.Value, request.SuggestionIds!, ct);
        return result switch
        {
            AlternativeCopyResult.Ok => TypedResults.Ok(response!),
            AlternativeCopyResult.NotFound => TypedResults.NotFound(),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Analysis running",
                detail: "Wait for the analysis run to finish, then copy."),
        };
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
