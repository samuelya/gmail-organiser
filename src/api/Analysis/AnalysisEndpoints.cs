using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Policies;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Analysis;

/// <summary>Analysis runs, re-analyse and the summary; also maps the preview and prompt endpoints.</summary>
public static class AnalysisEndpoints
{
    public const int DefaultListLimit = 50;

    public static IEndpointRouteBuilder MapAnalysisEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAnalysisPromptEndpoints();
        endpoints.MapAnalysisPreviewEndpoints();

        var group = endpoints.MapGroup("/api/analysis").WithTags("Analysis");
        group.MapPost("/runs", StartAsync).RequireAccountMatch();
        group.MapGet("/runs", ListAsync);
        group.MapGet("/runs/{id:guid}", GetAsync);
        group.MapPost("/runs/{id:guid}/cancel", CancelAsync);
        group.MapPost("/runs/{id:guid}/resume", ResumeAsync).RequireAccountMatch();
        group.MapPost("/compare-runs", StartCompareAsync).RequireAccountMatch();
        group.MapPost("/re-analyse", ReanalyseAsync).RequireAccountMatch();
        group.MapGet("/summary", async (AnalysisRunService runs, CancellationToken ct) => TypedResults.Ok(await runs.SummaryAsync(ct)));
        return endpoints;
    }

    /// <summary>202 with the queued run; 400 on an invalid request; 409 when no chat model is selected.</summary>
    private static async Task<Results<Accepted<AnalysisRunDto>, ValidationProblem>> StartAsync(
        StartAnalysisRunRequest request, AnalysisRunService runs, ISettingsStore settingsStore, AppDbContext db,
        SenderProfileBuilder profiles, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var settings = await settingsStore.GetAsync(ct);
        var (scope, count) = AnalysisPreviewEndpoint.ValidateSelection(
            request.Scope, request.SenderAddress, request.MessageIds, request.Count, settings, errors);
        var groupingMode = ParseGroupingMode(request.GroupingMode, errors);
        if (scope == AnalysisScope.Sender && !errors.ContainsKey("senderAddress"))
        {
            var address = request.SenderAddress!.Trim().ToLowerInvariant();
            if (!await db.Senders.AnyAsync(s => s.Address == address, ct))
            {
                errors["senderAddress"] = [AnalysisPreviewEndpoint.UnknownSender];
            }
        }

        var senderAddress = request.SenderAddress;
        if (scope == AnalysisScope.TopSenders && senderAddress is not null && !errors.ContainsKey("senderAddress"))
        {
            // The run stores the canonical address, so "Propose policy" works from a raw relay address too.
            senderAddress = await profiles.ResolveSenderAsync(senderAddress.Trim().ToLowerInvariant(), ct);
            if (senderAddress is null)
            {
                errors["senderAddress"] = [AnalysisPreviewEndpoint.UnknownSender];
            }
        }

        if (errors.Count > 0 || scope is not { } s)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var run = await runs.StartAsync(s, senderAddress, request.MessageIds, count, groupingMode, ct);
        return TypedResults.Accepted($"/api/analysis/runs/{run.Id}", run);
    }

    /// <summary>
    /// 202 with the queued compare run; 400 unless exactly one of <c>suggestionIds</c> or <c>runId</c> names 1 to the max
    /// count suggestions; 409 when no chat model is selected.
    /// </summary>
    private static async Task<Results<Accepted<AnalysisRunDto>, ValidationProblem>> StartCompareAsync(
        CompareRunRequest request, AnalysisRunService runs, CancellationToken ct)
    {
        var max = SettingsValidation.MaxAnalysisDefaultCount;
        if ((request.SuggestionIds is null) == (request.RunId is null))
        {
            return Invalid("", "Give either suggestionIds or runId.");
        }

        if (request.SuggestionIds is { } ids && (ids.Length == 0 || ids.Distinct().Count() > max))
        {
            return Invalid("suggestionIds", $"1 to {max} suggestion ids.");
        }

        var (result, run) = await runs.StartCompareAsync(request.SuggestionIds, request.RunId, ct);
        return result switch
        {
            CompareRunResult.Ok => TypedResults.Accepted($"/api/analysis/runs/{run!.Id}", run),
            CompareRunResult.RunNotFound => Invalid("runId", "No such run."),
            CompareRunResult.TooMany => Invalid("runId", $"The run has more than {max} suggestions; select at most {max}."),
            _ => Invalid(request.RunId is null ? "suggestionIds" : "runId", "No suggestions to compare."),
        };

        static ValidationProblem Invalid(string field, string message) =>
            TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
    }

    /// <summary>
    /// Newest first; <c>active</c> filters queued/running (true) or finished (false) runs, <c>status</c>
    /// (<c>queued | running | completed | failed | cancelled</c>) one status.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<AnalysisRunDto>>, ValidationProblem>> ListAsync(
        AnalysisRunService runs, CancellationToken ct, bool? active = null, string? status = null, int limit = DefaultListLimit)
    {
        if (limit is < 1 or > AnalysisRunService.MaxListLimit)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["limit"] = [$"Must be between 1 and {AnalysisRunService.MaxListLimit}."],
            });
        }

        AnalysisRunStatus? parsed = null;
        if (status is not null)
        {
            if (!SnakeCaseEnumConverter<AnalysisRunStatus>.TryFromDb(status, out var match))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = [$"Must be one of {SnakeCaseEnumConverter<AnalysisRunStatus>.NamesList}."],
                });
            }

            parsed = match;
        }

        return TypedResults.Ok(await runs.ListAsync(active, parsed, limit, ct));
    }

    private static async Task<Results<Ok<AnalysisRunDto>, NotFound>> GetAsync(Guid id, AnalysisRunService runs, CancellationToken ct) =>
        await runs.GetAsync(id, ct) is { } run ? TypedResults.Ok(run) : TypedResults.NotFound();

    /// <summary>200 with the run (a running one stops after its current group); 404; 409 when it has already finished.</summary>
    private static async Task<Results<Ok<AnalysisRunDto>, NotFound, ProblemHttpResult>> CancelAsync(
        Guid id, AnalysisRunService runs, CancellationToken ct)
    {
        var (result, run) = await runs.CancelAsync(id, ct);
        return result switch
        {
            JobActionResult.Ok => TypedResults.Ok(run!),
            JobActionResult.NotFound => TypedResults.NotFound(),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "Run finished", detail: "The run has already finished."),
        };
    }

    /// <summary>
    /// 202 with the new job of a failed or stalled run, continuing from its cursor and retrying its failed messages once;
    /// 404; 409 when the run is running, completed or cancelled, or no chat model is selected.
    /// </summary>
    private static async Task<Results<Accepted<JobDto>, NotFound, ProblemHttpResult>> ResumeAsync(
        Guid id, AnalysisRunService runs, CancellationToken ct)
    {
        var (result, job) = await runs.ResumeAsync(id, ct);
        return result switch
        {
            ResumeRunResult.Ok => TypedResults.Accepted($"/api/jobs/{job!.Id}", job),
            ResumeRunResult.NotFound => TypedResults.NotFound(),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Run not resumable",
                detail: "Only a failed or stalled run can be resumed."),
        };
    }

    /// <summary>200 with how many messages were reset; 400 unless exactly one selector is given; 409 when every match is approved or applied.</summary>
    private static async Task<Results<Ok<ReanalyseResponse>, ValidationProblem, ProblemHttpResult>> ReanalyseAsync(
        ReanalyseRequest request, AnalysisRunService runs, CancellationToken ct)
    {
        var hasIds = request.MessageIds is not null;
        var hasSender = request.SenderAddress is not null;
        if (hasIds == hasSender)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [""] = ["Give either messageIds or senderAddress."],
            });
        }

        if (hasIds && !AnalysisPreviewEndpoint.AreValidMessageIds(request.MessageIds))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["messageIds"] = [$"1 to {AnalysisCandidates.MaxMessageIds} ids of letters, digits, '-' or '_', at most {AnalysisPreviewEndpoint.MaxMessageIdLength} characters each."],
            });
        }

        if (hasSender && (string.IsNullOrWhiteSpace(request.SenderAddress) || request.SenderAddress.Length > AnalysisPreviewEndpoint.MaxSenderAddressLength))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["senderAddress"] = [$"Required, at most {AnalysisPreviewEndpoint.MaxSenderAddressLength} characters."],
            });
        }

        var (result, reset) = await runs.ReanalyseAsync(request.MessageIds, request.SenderAddress, ct);
        return result == ReanalyseResult.OnlyDecided
            ? TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Already decided",
                detail: "Every matching suggestion is approved or applied; nothing was reset.")
            : TypedResults.Ok(new ReanalyseResponse(reset));
    }

    private static AnalysisGroupingMode? ParseGroupingMode(string? value, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (SnakeCaseEnumConverter<AnalysisGroupingMode>.TryFromDb(value, out var mode))
        {
            return mode;
        }

        errors["groupingMode"] = [$"Must be one of {SnakeCaseEnumConverter<AnalysisGroupingMode>.NamesList}."];
        return null;
    }
}
