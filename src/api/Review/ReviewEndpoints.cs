using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Memory;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Review;

/// <summary>Review by sender and group: approve, edit, reject, bulk approve and analyse individually. No Gmail calls.</summary>
public static class ReviewEndpoints
{
    public const int MaxGroupKeyLength = 2000;

    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/review").WithTags("Review");
        group.MapGet("/senders", ListSendersAsync);
        group.MapGet("/senders/{address}", GetSenderAsync);
        group.MapPost("/suggestions/{id:guid}/approve", (Guid id, ReviewService review, CancellationToken ct) =>
            ToResultAsync(review.DecideAsync(id, DecisionOutcome.Approved, ct)));
        group.MapPost("/suggestions/{id:guid}/reject", (Guid id, ReviewService review, CancellationToken ct) =>
            ToResultAsync(review.DecideAsync(id, DecisionOutcome.Rejected, ct)));
        group.MapPut("/suggestions/{id:guid}", EditAsync);
        group.MapPost("/groups/approve", (GroupDecisionRequest request, ReviewService review, CancellationToken ct) =>
            DecideGroupAsync(request, DecisionOutcome.Approved, review, ct));
        group.MapPost("/groups/reject", (GroupDecisionRequest request, ReviewService review, CancellationToken ct) =>
            DecideGroupAsync(request, DecisionOutcome.Rejected, review, ct));
        group.MapPost("/bulk-approve", BulkApproveAsync);
        group.MapPost("/analyse-individually", AnalyseIndividuallyAsync).RequireAccountMatch();
        group.MapApplyEndpoints();
        return endpoints;
    }

    /// <summary>Senders with suggestions in <c>status</c> (default pending), most of them first; <c>search</c> as on the senders page.</summary>
    private static async Task<Results<Ok<PagedDto<ReviewSenderDto>>, ValidationProblem>> ListSendersAsync(
        ReviewQuery query, CancellationToken ct, string? search = null, int? page = null, int? pageSize = null, string? status = null)
    {
        var paging = SenderQuery.Parse(search, page, pageSize, null, null, out var errors);
        var parsed = ReviewQuery.ParseStatus(status);
        if (parsed is null)
        {
            errors["status"] = ["Must be pending, approved or rejected."];
        }

        return paging is null || parsed is not { } s
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await query.ListAsync(s, paging.Search, paging.Page, paging.PageSize, ct));
    }

    /// <summary>One page of the sender's groups in <c>status</c>, largest first; 404 when the sender has no suggestions.</summary>
    private static async Task<Results<Ok<ReviewSenderDetailDto>, NotFound, ValidationProblem>> GetSenderAsync(
        string address, ReviewQuery query, CancellationToken ct, string? status = null, int? page = null, int? pageSize = null)
    {
        var errors = new Dictionary<string, string[]>();
        if (page is < 1 or > SenderQuery.MaxPage)
        {
            errors["page"] = [$"Must be between 1 and {SenderQuery.MaxPage}."];
        }

        if (pageSize is < 1 or > ReviewQuery.MaxGroupPageSize)
        {
            errors["pageSize"] = [$"Must be between 1 and {ReviewQuery.MaxGroupPageSize}."];
        }

        var parsed = ReviewQuery.ParseStatus(status);
        if (parsed is null)
        {
            errors["status"] = ["Must be pending, approved or rejected."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        return Normalise(address) is { } a
            && await query.DetailAsync(a, parsed!.Value, page ?? 1, pageSize ?? ReviewQuery.DefaultGroupPageSize, ct) is { } detail
            ? TypedResults.Ok(detail)
            : TypedResults.NotFound();
    }

    /// <summary>Saves the edited outcome and approves it; 400 on an invalid label path or a missing flag, 404, 409 when applied.</summary>
    private static async Task<Results<Ok<SuggestionDto>, ValidationProblem, ProblemHttpResult>> EditAsync(
        Guid id, EditSuggestionRequest request, ReviewService review, CancellationToken ct)
    {
        var errors = OutcomeErrors(request.TopicLabel, request.NeedsAction, request.ToBeDeleted, out var label);
        return errors.Count > 0
            ? TypedResults.ValidationProblem(errors)
            : await ToResultAsync(review.EditAsync(id, label!, request.NeedsAction!.Value, request.ToBeDeleted!.Value, ct));
    }

    /// <summary>Reject takes every pending member; approve needs the card's outcome and takes the members that have it.</summary>
    private static async Task<Results<Ok<GroupDecisionResponse>, ValidationProblem>> DecideGroupAsync(
        GroupDecisionRequest request, DecisionOutcome outcome, ReviewService review, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        GroupOutcome? shown = null;
        if (outcome == DecisionOutcome.Approved)
        {
            errors = OutcomeErrors(request.TopicLabel, request.NeedsAction, request.ToBeDeleted, out var label);
            shown = errors.Count == 0 ? new GroupOutcome(label!, request.NeedsAction!.Value, request.ToBeDeleted!.Value) : null;
        }

        var sender = Normalise(request.SenderAddress);
        if (sender is null)
        {
            errors["senderAddress"] = [$"Required, at most {AnalysisPreviewEndpoint.MaxSenderAddressLength} characters."];
        }

        if (string.IsNullOrEmpty(request.GroupKey) || request.GroupKey.Length > MaxGroupKeyLength)
        {
            errors["groupKey"] = [$"Required, at most {MaxGroupKeyLength} characters."];
        }

        return errors.Count > 0
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await review.DecideGroupAsync(sender!, request.GroupKey!, outcome, shown, ct));
    }

    /// <summary>Approves pending model suggestions at or above the threshold (and derived/memory ones when asked).</summary>
    private static async Task<Results<Ok<BulkApproveResponse>, ValidationProblem>> BulkApproveAsync(
        BulkApproveRequest request, ReviewService review, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Threshold is { } t
            && (!double.IsFinite(t) || t < SettingsValidation.MinBulkApproveThreshold || t > SettingsValidation.MaxBulkApproveThreshold))
        {
            errors["threshold"] = [$"Must be between {SettingsValidation.MinBulkApproveThreshold} and {SettingsValidation.MaxBulkApproveThreshold}."];
        }

        var sender = request.SenderAddress is null ? null : Normalise(request.SenderAddress);
        if (request.SenderAddress is not null && sender is null)
        {
            errors["senderAddress"] = [$"At most {AnalysisPreviewEndpoint.MaxSenderAddressLength} characters, not blank."];
        }

        return errors.Count > 0
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await review.BulkApproveAsync(request.Threshold, request.IncludeDerived, sender, ct));
    }

    /// <summary>202 with the queued run; 404 when no suggestion matches; 409 when all are approved or applied (or no chat model).</summary>
    private static async Task<Results<Accepted<AnalysisRunDto>, ValidationProblem, NotFound, ProblemHttpResult>> AnalyseIndividuallyAsync(
        AnalyseIndividuallyRequest request, ReviewService review, CancellationToken ct)
    {
        if (request.SuggestionIds is not { Length: > 0 and <= AnalysisCandidates.MaxMessageIds } ids)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["suggestionIds"] = [$"1 to {AnalysisCandidates.MaxMessageIds} ids."],
            });
        }

        var (result, run) = await review.AnalyseIndividuallyAsync([.. ids.Distinct()], ct);
        return result switch
        {
            ReviewResult.Ok => TypedResults.Accepted($"/api/analysis/runs/{run!.Id}", run),
            ReviewResult.NotFound => TypedResults.NotFound(),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Already decided",
                detail: "Every selected suggestion is approved or applied; nothing was reset."),
        };
    }

    private static async Task<Results<Ok<SuggestionDto>, ValidationProblem, ProblemHttpResult>> ToResultAsync(
        Task<(ReviewResult Result, SuggestionDto? Suggestion)> action) =>
        await action switch
        {
            (ReviewResult.Ok, { } s) => TypedResults.Ok(s),
            (ReviewResult.NotFound, _) => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Suggestion not found"),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Already applied",
                detail: "An applied suggestion can no longer be changed."),
        };

    /// <summary>A valid, non-system label path (trimmed into <paramref name="label"/>) and both flags present.</summary>
    private static Dictionary<string, string[]> OutcomeErrors(string? topicLabel, bool? needsAction, bool? toBeDeleted, out string? label)
    {
        var errors = new Dictionary<string, string[]>();
        label = topicLabel?.Trim();
        if (label is null || !LabelPath.IsValid(label) || LabelPath.IsReserved(label))
        {
            errors["topicLabel"] = [$"Up to five '/'-separated parts, at most {GmailLimits.LabelNameMaxLength} characters, not a Gmail system label."];
        }

        if (needsAction is null)
        {
            errors["needsAction"] = ["Required."];
        }

        if (toBeDeleted is null)
        {
            errors["toBeDeleted"] = ["Required."];
        }

        return errors;
    }

    /// <summary>Trimmed lower-case address, or null when blank or too long.</summary>
    private static string? Normalise(string? address) =>
        string.IsNullOrWhiteSpace(address) || address.Length > AnalysisPreviewEndpoint.MaxSenderAddressLength
            ? null
            : address.Trim().ToLowerInvariant();
}
