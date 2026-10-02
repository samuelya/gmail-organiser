using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Fetch;

/// <summary>Mailbox fetch start and status. Pause, resume and cancel are the generic <c>/api/jobs/{id}/...</c> endpoints.</summary>
public static class FetchEndpoints
{
    public static IEndpointRouteBuilder MapFetchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/fetch").WithTags("Fetch");
        group.MapPost("/mailbox/start", StartMailboxAsync);
        group.MapGet("/status", GetStatusAsync);
        return endpoints;
    }

    /// <summary>202 with a new job, 200 with the already active one, 409 when Gmail is not connected.</summary>
    private static async Task<Results<Accepted<StartFetchResponse>, Ok<StartFetchResponse>, ProblemHttpResult>> StartMailboxAsync(
        ITokenStore tokens, IJobService jobs, AppDbContext db, CancellationToken ct)
    {
        if (await tokens.GetAsync(ct) is not { ReauthRequired: false })
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Gmail not connected",
                detail: "Connect Gmail in Setup before fetching the mailbox.");
        }

        var activeId = await db.Jobs.AsNoTracking()
            .Where(j => j.Type == MailboxFetchJob.JobType && JobRow.Active.Contains(j.Status))
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(ct);
        if (activeId is { } id)
        {
            return TypedResults.Ok(new StartFetchResponse(id));
        }

        // A concurrent start that loses the insert gets the winner's job back, so there is never a second one.
        var job = await jobs.EnqueueAsync(MailboxFetchJob.JobType, MailboxFetchJob.Queue, null, ct);
        return TypedResults.Accepted($"/api/jobs/{job.Id}", new StartFetchResponse(job.Id));
    }

    private static async Task<Ok<FetchStatusDto>> GetStatusAsync(AppDbContext db, CancellationToken ct)
    {
        var state = await db.FetchState.AsNoTracking().SingleAsync(s => s.Id == FetchStateRow.SingletonId, ct);
        var stored = await db.Messages.LongCountAsync(m => !m.DeletedInGmail, ct);
        var senders = await db.Senders.LongCountAsync(ct);
        var active = await db.Jobs.AsNoTracking()
            .Where(j => j.Queue == JobQueues.Fetch && JobRow.Active.Contains(j.Status))
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);
        var activeJob = active.Find(j => j.Status == JobStatus.Running) ?? active.FirstOrDefault();

        return TypedResults.Ok(new FetchStatusDto(
            state.AccountEmail,
            SnakeCaseEnumConverter<MailboxPhase>.ToDb(state.MailboxPhase),
            state.InboxFetched,
            state.AllMailFetched,
            state.MessagesTotal,
            stored,
            senders,
            state.LastHistoryId,
            state.StartedAt,
            state.CompletedAt,
            activeJob?.ToDto()));
    }
}
