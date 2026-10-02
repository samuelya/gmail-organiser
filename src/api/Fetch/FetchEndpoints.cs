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
        group.MapPost("/mailbox/start", StartMailboxAsync).RequireAccountMatch();
        group.MapGet("/status", GetStatusAsync);
        return endpoints;
    }

    /// <summary>
    /// 202 with a new job; 200 with the active one, or with the latest failed or paused one after resuming it from its
    /// cursor; 409 when the local data belongs to another account (<see cref="AccountGuardEndpointExtensions.RequireAccountMatch"/>), Gmail is not connected or the mailbox is already fetched (that is the incremental fetch's job).
    /// </summary>
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

        // A new job would reset fetch_state and discard the failed or paused job's checkpoint.
        var latest = await LatestMailboxJobAsync(db, ct);
        if (latest is { Status: JobStatus.Failed or JobStatus.Paused }
            && await jobs.ResumeAsync(latest.Id, ct) == JobActionResult.Ok)
        {
            return TypedResults.Ok(new StartFetchResponse(latest.Id));
        }

        if (latest is null || !JobRow.Active.Contains(latest.Status))
        {
            var phase = await db.FetchState.Where(s => s.Id == FetchStateRow.SingletonId).Select(s => s.MailboxPhase).SingleAsync(ct);
            if (phase == MailboxPhase.Completed)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Mailbox already fetched",
                    detail: "The full mailbox fetch has completed; use the incremental fetch.");
            }
        }

        // A concurrent start that loses the insert gets the winner's job back with Created false.
        var (job, created) = await jobs.EnqueueAsync(MailboxFetchJob.JobType, MailboxFetchJob.Queue, null, ct);
        var response = new StartFetchResponse(job.Id);
        return created ? TypedResults.Accepted($"/api/jobs/{job.Id}", response) : TypedResults.Ok(response);
    }

    private static async Task<Ok<FetchStatusDto>> GetStatusAsync(AppDbContext db, ITokenStore tokens, CancellationToken ct)
    {
        var counts = await db.FetchState.AsNoTracking()
            .Where(s => s.Id == FetchStateRow.SingletonId)
            .Select(s => new
            {
                State = s,
                Stored = db.Messages.LongCount(m => !m.DeletedInGmail),
                Senders = db.Senders.LongCount(),
            })
            .SingleAsync(ct);
        var state = counts.State;
        var latest = await LatestMailboxJobAsync(db, ct);
        var check = AccountGuard.Compare(state.AccountEmail, (await tokens.GetAsync(ct))?.AccountEmail);

        return TypedResults.Ok(new FetchStatusDto(
            state.AccountEmail,
            SnakeCaseEnumConverter<MailboxPhase>.ToDb(state.MailboxPhase),
            state.InboxFetched,
            state.AllMailFetched,
            state.MessagesTotal,
            counts.Stored,
            counts.Senders,
            state.LastHistoryId,
            state.StartedAt,
            state.CompletedAt,
            latest is not null && JobRow.Active.Contains(latest.Status) ? latest.ToDto() : null,
            latest is { Status: JobStatus.Failed } ? latest.ToDto() : null,
            check.IsMismatch,
            check.LocalAccountMasked));
    }

    /// <summary>The active mailbox fetch job (at most one, by the unique index), else the most recent one.</summary>
    private static Task<JobRow?> LatestMailboxJobAsync(AppDbContext db, CancellationToken ct) =>
        db.Jobs.AsNoTracking()
            .Where(j => j.Type == MailboxFetchJob.JobType)
            .OrderByDescending(j => JobRow.Active.Contains(j.Status))
            .ThenByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
}
