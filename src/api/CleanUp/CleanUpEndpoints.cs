using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.CleanUp;

/// <summary>The Clean-up page: the delete-labelled mail by sender, removing it from the list, and Delete (to Trash).</summary>
public static class CleanUpEndpoints
{
    public const int MaxMessageIds = 5000;
    public const int MaxMessageIdLength = 100;

    public static IEndpointRouteBuilder MapCleanUpEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/clean-up").WithTags("Clean-up");
        group.MapGet("/summary", SummaryAsync);
        group.MapGet("/senders", SendersAsync);
        group.MapGet("/senders/{address}/messages", MessagesAsync);
        group.MapPost("/unmark", (CleanupSelectionRequest request, CleanUpService cleanUp, CancellationToken ct) =>
            StartAsync(ActionKind.Unmark, request, cleanUp, ct)).RequireAccountMatch();
        group.MapPost("/delete", (CleanupSelectionRequest request, CleanUpService cleanUp, CancellationToken ct) =>
            StartAsync(ActionKind.Trash, request, cleanUp, ct)).RequireAccountMatch();
        group.MapGet("/retention", RetentionAsync);
        group.MapPost("/retention/run", RunRetentionAsync).RequireAccountMatch();
        return endpoints;
    }

    /// <summary>Counts of the delete-labelled messages; 503 when Gmail is not connected.</summary>
    private static async Task<Results<Ok<CleanupSummaryDto>, ProblemHttpResult>> SummaryAsync(CleanUpQuery query, CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await query.SummaryAsync(ct));
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailProblems.NotConnected(ex);
        }
    }

    /// <summary>Senders of delete-labelled mail, most messages first.</summary>
    private static async Task<Results<Ok<PagedDto<CleanupSenderDto>>, ValidationProblem, ProblemHttpResult>> SendersAsync(
        CleanUpQuery query, CancellationToken ct, string? search = null, int? page = null, int? pageSize = null)
    {
        if (SenderQuery.Parse(search, page, pageSize, null, null, out var errors) is not { } paging)
        {
            return TypedResults.ValidationProblem(errors);
        }

        try
        {
            return TypedResults.Ok(await query.SendersAsync(paging.Search, paging.Page, paging.PageSize, ct));
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailProblems.NotConnected(ex);
        }
    }

    /// <summary>The sender's delete-labelled messages, newest first.</summary>
    private static async Task<Results<Ok<PagedDto<CleanupMessageDto>>, ValidationProblem, ProblemHttpResult>> MessagesAsync(
        string address, CleanUpQuery query, CancellationToken ct, int? page = null, int? pageSize = null)
    {
        var paging = SenderQuery.Parse(null, page, pageSize, null, null, out var errors);
        var normalised = SenderAllowlist.Normalise(address, out var addressError);
        if (addressError is not null)
        {
            errors["address"] = [addressError];
        }

        if (paging is null || normalised is null)
        {
            return TypedResults.ValidationProblem(errors);
        }

        try
        {
            return TypedResults.Ok(await query.MessagesAsync(normalised, paging.Page, paging.PageSize, ct));
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailProblems.NotConnected(ex);
        }
    }

    /// <summary>202 with the batch (its job id included); 204 when no selected message qualifies; 400 for a bad selection.</summary>
    private static async Task<Results<Accepted<CleanupBatchDto>, NoContent, ValidationProblem, ProblemHttpResult>> StartAsync(
        ActionKind kind, CleanupSelectionRequest request, CleanUpService cleanUp, CancellationToken ct)
    {
        if (Parse(request, out var errors) is not { } selection)
        {
            return TypedResults.ValidationProblem(errors);
        }

        try
        {
            return await cleanUp.StartAsync(kind, selection, request.IncludeProtected, ct) is { } started
                ? TypedResults.Accepted($"/api/jobs/{started.Batch.JobId}", started)
                : TypedResults.NoContent();
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailProblems.NotConnected(ex);
        }
    }

    /// <summary>Whether retention is on, the last and next sweep, and how many messages one would mark now; 503 when Gmail is not connected.</summary>
    private static async Task<Results<Ok<RetentionStatusDto>, ProblemHttpResult>> RetentionAsync(RetentionService retention, CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await retention.StatusAsync(ct));
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailProblems.NotConnected(ex);
        }
    }

    /// <summary>202 with the queued sweep; 409 while retention is off or a sweep is queued, running or paused.</summary>
    private static async Task<Results<Accepted<JobDto>, ProblemHttpResult>> RunRetentionAsync(RetentionService retention, CancellationToken ct) =>
        await retention.RunNowAsync(ct) switch
        {
            ({ } job, RetentionRunRefusal.None) => TypedResults.Accepted($"/api/jobs/{job.Id}", job),
            (_, RetentionRunRefusal.Disabled) => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "Retention is off", detail: "Turn retention on in Settings first."),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "A retention sweep is already active", detail: "Wait for it to finish, or resume or cancel it in Jobs."),
        };

    /// <summary>The selection, or null with <paramref name="errors"/>: exactly one of message ids, a sender or all.</summary>
    private static CleanUpSelection? Parse(CleanupSelectionRequest request, out Dictionary<string, string[]> errors)
    {
        errors = [];
        var given = (request.MessageIds is null ? 0 : 1) + (request.SenderAddress is null ? 0 : 1) + (request.All ? 1 : 0);
        if (given != 1)
        {
            errors["selection"] = ["Give exactly one of messageIds, senderAddress or all: true."];
            return null;
        }

        if (request.MessageIds is { } ids)
        {
            if (ids.Length is 0 or > MaxMessageIds || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > MaxMessageIdLength))
            {
                errors["messageIds"] = [$"1 to {MaxMessageIds} message ids, each 1 to {MaxMessageIdLength} characters."];
                return null;
            }

            return new CleanUpSelection(MessageIds: [.. ids.Distinct(StringComparer.Ordinal)]);
        }

        if (request.SenderAddress is not null)
        {
            var sender = SenderAllowlist.Normalise(request.SenderAddress, out var error);
            if (error is not null)
            {
                errors["senderAddress"] = [error];
                return null;
            }

            return new CleanUpSelection(SenderAddress: sender);
        }

        return new CleanUpSelection();
    }
}
