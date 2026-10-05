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

    /// <summary>Most replaced labels one edit names.</summary>
    public const int MaxReplaceLabels = 100;

    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/review").WithTags("Review");
        group.MapGet("/senders", ListSendersAsync);
        group.MapGet("/senders/{address}", GetSenderAsync);
        group.MapGet("/senders/{address}/pattern", GetPatternAsync);
        group.MapPost("/senders/{address}/apply-rest", ApplyRestAsync).RequireAccountMatch();
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
        group.MapAlternativeEndpoints();
        return endpoints;
    }

    /// <summary>
    /// Senders with suggestions in <c>status</c> (default pending), most of them first; <c>search</c> as on the senders
    /// page; <c>hasAlternative=true</c> counts only suggestions with a compare-run alternative.
    /// </summary>
    private static async Task<Results<Ok<PagedDto<ReviewSenderDto>>, ValidationProblem>> ListSendersAsync(
        ReviewQuery query, CancellationToken ct, string? search = null, int? page = null, int? pageSize = null, string? status = null,
        bool hasAlternative = false)
    {
        var paging = SenderQuery.Parse(search, page, pageSize, null, null, out var errors);
        var parsed = ReviewQuery.ParseStatus(status);
        if (parsed is null)
        {
            errors["status"] = ["Must be pending, approved, rejected or applied."];
        }

        return paging is null || parsed is not { } s
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await query.ListAsync(s, paging.Search, paging.Page, paging.PageSize, ct, hasAlternative));
    }

    /// <summary>
    /// One page of the sender's groups in <c>status</c>, largest first (<c>hasAlternative=true</c>: only suggestions with
    /// a compare-run alternative); 404 when the sender has no such suggestions.
    /// </summary>
    private static async Task<Results<Ok<ReviewSenderDetailDto>, NotFound, ValidationProblem>> GetSenderAsync(
        string address, ReviewQuery query, CancellationToken ct, string? status = null, int? page = null, int? pageSize = null,
        bool hasAlternative = false)
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
            errors["status"] = ["Must be pending, approved, rejected or applied."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        return Normalise(address) is { } a
            && await query.DetailAsync(a, parsed!.Value, page ?? 1, pageSize ?? ReviewQuery.DefaultGroupPageSize, ct, hasAlternative) is { } detail
            ? TypedResults.Ok(detail)
            : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<SenderPatternDto>, ValidationProblem>> GetPatternAsync(
        string address, SenderPatternService patterns, CancellationToken ct) =>
        Normalise(address) is { } a
            ? TypedResults.Ok(await patterns.GetAsync(a, ct))
            : TypedResults.ValidationProblem(AddressError());

    /// <summary>
    /// 202 with the queued batch, 200 when nothing remained; 400 when the requested document type is the topic label
    /// applied (the pattern's when none is given); 409 without a pattern or topic label, or on a race.
    /// </summary>
    private static async Task<Results<Accepted<ApplyRestResponse>, Ok<ApplyRestResponse>, ValidationProblem, ProblemHttpResult>> ApplyRestAsync(
        string address, ApplyRestRequest? request, SenderPatternService patterns, ISettingsStore settings, CancellationToken ct)
    {
        request ??= new();
        var errors = Normalise(address) is null ? AddressError() : [];
        if (request.TopicLabel is { } label && !LabelResolver.IsValid(label.Trim()))
        {
            errors["topicLabel"] = [$"Up to five '/'-separated parts, at most {GmailLimits.LabelNameMaxLength} characters, not a Gmail system label."];
        }

        var type = request.DocumentTypeLabel is null
            ? DocumentTypeChange.Unchanged
            : DocumentTypeEdit.Validate(request.DocumentTypeLabel, (await settings.GetAsync(ct)).DocumentTypeParent, request.TopicLabel, errors);

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        return await patterns.ApplyRestAsync(Normalise(address)!, request, type, ct) switch
        {
            (ApplyRestStatus.DocumentTypeIsTopic, _) => DocumentTypeIsTopic(),
            (ApplyRestStatus.Ok, { Batch: { } batch } r) => TypedResults.Accepted($"/api/jobs/{batch.JobId}", r),
            (ApplyRestStatus.Ok, { } r) => TypedResults.Ok(r),
            (ApplyRestStatus.NoPattern, _) => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "No pattern",
                detail: "The sender has no approved outcome with a label Gmail accepts; pass a topic label."),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Messages changed",
                detail: "Some of the sender's messages got a suggestion meanwhile; nothing was created. Try again."),
        };
    }

    /// <summary>
    /// Saves the edited outcome and approves it; 400 on an invalid label path or a missing flag, 404, 409 when applied,
    /// then 400 naming the replaced labels the message does not carry and 503 when the Gmail label list cannot be loaded.
    /// A document-type label needs the document-type parent setting (<see cref="DocumentTypeEdit.Validate"/>); 400 too
    /// when the topic label would equal the document type the suggestion keeps, and when it is the delete label on an
    /// email not to be deleted (any source).
    /// </summary>
    private static async Task<Results<Ok<SuggestionDto>, ValidationProblem, ProblemHttpResult>> EditAsync(
        Guid id, EditSuggestionRequest request, ReviewService review, ISettingsStore settings, CancellationToken ct)
    {
        var errors = OutcomeErrors(request.TopicLabel, request.NeedsAction, request.ToBeDeleted, out var label);
        ReplaceLabelsShapeErrors(request.ReplaceLabels, errors);
        var current = await settings.GetAsync(ct);
        if (request.ToBeDeleted == false && ActionPlanner.IsDeleteTopicNotDeleted(label, false, current.DeleteLabelName))
        {
            errors["topicLabel"] = [ActionPlanner.DeleteTopicError];
        }

        var type = request.DocumentTypeLabel is null
            ? DocumentTypeChange.Unchanged
            : DocumentTypeEdit.Validate(request.DocumentTypeLabel, current.DocumentTypeParent, label, errors);
        var mailType = MailTypeChange.Validate(request.MailType, errors);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var (result, suggestion, unknown) = await review.EditAsync(
            id, label!, request.NeedsAction!.Value, request.ToBeDeleted!.Value, request.ReplaceLabels, type, mailType, ct);
        return result == ReviewResult.InvalidReplaceLabels
            ? ReplaceLabelsUnknown("the email does not carry", unknown)
            : ToResult((result, suggestion));
    }

    /// <summary>Reject takes every pending member; approve needs the card's outcome and takes the members that have it.</summary>
    private static async Task<Results<Ok<GroupDecisionResponse>, ValidationProblem, ProblemHttpResult>> DecideGroupAsync(
        GroupDecisionRequest request, DecisionOutcome outcome, ReviewService review, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        GroupOutcome? shown = null;
        if (outcome == DecisionOutcome.Approved)
        {
            errors = OutcomeErrors(request.TopicLabel, request.NeedsAction, request.ToBeDeleted, out var label);
            ReplaceLabelsShapeErrors(request.ReplaceLabels, errors);
            var type = string.IsNullOrWhiteSpace(request.DocumentTypeLabel) ? null : request.DocumentTypeLabel.Trim();
            if (type is not null && !LabelPath.IsValid(type))
            {
                errors["documentTypeLabel"] = [$"Up to five '/'-separated parts, at most {GmailLimits.LabelNameMaxLength} characters."];
            }

            shown = errors.Count == 0
                ? new GroupOutcome(label!, request.NeedsAction!.Value, request.ToBeDeleted!.Value, request.ReplaceLabels, type)
                : null;
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

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (outcome == DecisionOutcome.Rejected)
        {
            return TypedResults.Ok(await review.DecideGroupAsync(sender!, request.GroupKey!, outcome, null, ct));
        }

        return await review.ApproveGroupAsync(sender!, request.GroupKey!, shown!, ct) switch
        {
            (ReviewResult.Ok, { } response, _) => TypedResults.Ok(response),
            (ReviewResult.InvalidReplaceLabels, _, var unknown) => ReplaceLabelsUnknown("no pending email of the group carries", unknown),
            _ => LabelsUnavailable(),
        };
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
        Task<(ReviewResult Result, SuggestionDto? Suggestion)> action) => ToResult(await action);

    private static Results<Ok<SuggestionDto>, ValidationProblem, ProblemHttpResult> ToResult(
        (ReviewResult Result, SuggestionDto? Suggestion) outcome) =>
        outcome switch
        {
            (ReviewResult.Ok, { } s) => TypedResults.Ok(s),
            (ReviewResult.NotFound, _) => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Suggestion not found"),
            (ReviewResult.LabelsUnavailable, _) => LabelsUnavailable(),
            (ReviewResult.DocumentTypeIsTopic, _) => DocumentTypeIsTopic(),
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

    /// <summary>At most <see cref="MaxReplaceLabels"/> non-blank names of at most the Gmail label name length.</summary>
    private static void ReplaceLabelsShapeErrors(string[]? replaceLabels, Dictionary<string, string[]> errors)
    {
        if (replaceLabels is not null
            && (replaceLabels.Length > MaxReplaceLabels
                || replaceLabels.Any(l => string.IsNullOrWhiteSpace(l) || l.Trim().Length > GmailLimits.LabelNameMaxLength)))
        {
            errors["replaceLabels"] = [$"At most {MaxReplaceLabels} label names of at most {GmailLimits.LabelNameMaxLength} characters, not blank."];
        }
    }

    /// <summary>The <c>replaceLabels</c> field error names each unknown label, quoted.</summary>
    private static ValidationProblem ReplaceLabelsUnknown(string what, IReadOnlyList<string> unknown) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["replaceLabels"] = [$"Names labels {what}: {string.Join(", ", unknown.Select(l => $"'{l}'"))}."],
        });

    private static ValidationProblem DocumentTypeIsTopic() => TypedResults.ValidationProblem(
        new Dictionary<string, string[]> { [DocumentTypeEdit.Field] = [DocumentTypeEdit.SameAsTopicMessage] });

    private static ProblemHttpResult LabelsUnavailable() => TypedResults.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Gmail labels unavailable",
        detail: "The Gmail label list could not be loaded; nothing was changed. Try again.");

    private static Dictionary<string, string[]> AddressError() =>
        new() { ["address"] = [$"At most {AnalysisPreviewEndpoint.MaxSenderAddressLength} characters, not blank."] };

    /// <summary>Trimmed lower-case address, or null when blank or too long.</summary>
    private static string? Normalise(string? address) =>
        string.IsNullOrWhiteSpace(address) || address.Length > AnalysisPreviewEndpoint.MaxSenderAddressLength
            ? null
            : address.Trim().ToLowerInvariant();
}
