using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Common;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Claude;

/// <summary>The Claude review queue: send items, list them, cancel, retry, accept or dismiss a verdict. No Gmail calls.</summary>
public static class ClaudeReviewEndpoints
{
    public static IEndpointRouteBuilder MapClaudeReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/claude/reviews").WithTags("Claude");
        group.MapPost("", CreateAsync);
        group.MapGet("", ListAsync);
        group.MapGet("/summary", (ExternalReviewQuery query, CancellationToken ct) => query.SummaryAsync(ct));
        group.MapGet("/{id:guid}", async Task<Results<Ok<ExternalReviewDto>, NotFound>> (Guid id, ExternalReviewQuery query, CancellationToken ct) =>
            await query.GetAsync(id, ct) is { } item ? TypedResults.Ok(item) : TypedResults.NotFound());
        group.MapPost("/{id:guid}/cancel", (Guid id, ExternalReviewService reviews, CancellationToken ct) =>
            ToResultAsync(reviews.CancelAsync(id, ct)));
        group.MapPost("/{id:guid}/accept", (Guid id, ExternalReviewService reviews, CancellationToken ct) =>
            ToResultAsync(reviews.AcceptAsync(id, ct)));
        group.MapPost("/{id:guid}/dismiss", (Guid id, ExternalReviewService reviews, CancellationToken ct) =>
            ToResultAsync(reviews.DismissAsync(id, ct)));
        group.MapPost("/{id:guid}/retry", (Guid id, ExternalReviewService reviews, CancellationToken ct) =>
            ToResultAsync(reviews.RetryAsync(id, ct)));
        return endpoints;
    }

    /// <summary>Queues one item per target without an open item; 400 when empty, invalid or over the limit; 404 for an unknown run.</summary>
    private static async Task<Results<Ok<CreateExternalReviewsResponse>, ValidationProblem, NotFound>> CreateAsync(
        CreateExternalReviewsRequest request, ExternalReviewService reviews, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var max = ExternalReviewService.MaxTargets;
        if (request.SuggestionIds is not { Length: > 0 } && request.Groups is not { Length: > 0 } && request.RunId is null)
        {
            errors["request"] = ["Pass suggestionIds, groups or runId."];
        }

        if (request.SuggestionIds is { Length: > ExternalReviewService.MaxTargets })
        {
            errors["suggestionIds"] = [$"At most {max} ids."];
        }

        var groups = new List<GroupRef>();
        if (request.Groups is { Length: > ExternalReviewService.MaxTargets })
        {
            errors["groups"] = [$"At most {max} groups."];
        }
        else
        {
            foreach (var g in request.Groups ?? [])
            {
                var sender = g?.SenderAddress?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(sender) || sender.Length > AnalysisPreviewEndpoint.MaxSenderAddressLength
                    || string.IsNullOrEmpty(g!.GroupKey) || g.GroupKey.Length > ReviewEndpoints.MaxGroupKeyLength)
                {
                    errors["groups"] = [$"Each group needs a sender address (at most {AnalysisPreviewEndpoint.MaxSenderAddressLength} characters) and a group key (at most {ReviewEndpoints.MaxGroupKeyLength})."];
                    break;
                }

                groups.Add(new GroupRef(sender, g.GroupKey));
            }
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        return await reviews.CreateAsync(request.SuggestionIds ?? [], [.. groups], request.RunId, ct) switch
        {
            (CreateExternalReviewsResult.Ok, { } response) => TypedResults.Ok(response),
            (CreateExternalReviewsResult.RunNotFound, _) => TypedResults.NotFound(),
            _ => TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = [$"At most {max} items after expanding the run."],
            }),
        };
    }

    /// <summary>Items in <c>status</c> (any when omitted), newest first.</summary>
    private static async Task<Results<Ok<PagedDto<ExternalReviewDto>>, ValidationProblem>> ListAsync(
        ExternalReviewQuery query, CancellationToken ct, string? status = null, int? page = null, int? pageSize = null)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ExternalReviewQuery.TryParseStatus(status, out var parsed))
        {
            errors["status"] = ["Must be queued, running, reviewed, unavailable or cancelled."];
        }

        if (page is < 1 or > SenderQuery.MaxPage)
        {
            errors["page"] = [$"Must be between 1 and {SenderQuery.MaxPage}."];
        }

        if (pageSize is < 1 or > ExternalReviewQuery.MaxPageSize)
        {
            errors["pageSize"] = [$"Must be between 1 and {ExternalReviewQuery.MaxPageSize}."];
        }

        return errors.Count > 0
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await query.ListAsync(parsed, page ?? 1, pageSize ?? ExternalReviewQuery.DefaultPageSize, ct));
    }

    private static async Task<Results<Ok<ExternalReviewDto>, ProblemHttpResult>> ToResultAsync(
        Task<(ExternalReviewResult Result, ExternalReviewDto? Item)> action) =>
        await action switch
        {
            (ExternalReviewResult.Ok, { } item) => TypedResults.Ok(item),
            (ExternalReviewResult.NotFound, _) => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Review item not found"),
            (ExternalReviewResult.AlreadyDecided, _) => Conflict("Already decided", "The suggestions are no longer pending."),
            (ExternalReviewResult.NeedsHuman, _) => Conflict("Needs a human", "Claude gave no verdict to accept; decide on the review page."),
            (ExternalReviewResult.InvalidVerdict, _) => Conflict("Invalid verdict", "Claude's label is not a valid label path; edit on the review page."),
            (_, var item) => Conflict("Wrong status", $"Not allowed while the item is {item?.Status ?? "in this status"}."),
        };

    private static ProblemHttpResult Conflict(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);
}
