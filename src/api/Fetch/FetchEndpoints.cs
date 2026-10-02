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
        group.MapPost("/incremental", StartIncrementalAsync).RequireAccountMatch();
        group.MapGet("/status", GetStatusAsync);
        group.MapPost("/sender", StartSenderAsync).RequireAccountMatch();
        return endpoints;
    }

    /// <summary>
    /// 202 with a new job; 200 with the active one, or with the latest failed or paused one after resuming it from its
    /// cursor; 409 when the local data belongs to another account (<see cref="AccountGuardEndpointExtensions.RequireAccountMatch"/>)
    /// or Gmail is not connected. Once the mailbox is fetched it starts the incremental fetch instead.
    /// </summary>
    private static async Task<Results<Accepted<StartFetchResponse>, Ok<StartFetchResponse>, ProblemHttpResult>> StartMailboxAsync(
        ITokenStore tokens, IJobService jobs, AppDbContext db, CancellationToken ct)
    {
        if (!await IsConnectedAsync(tokens, ct))
        {
            return GmailNotConnected("Connect Gmail in Setup before fetching the mailbox.");
        }

        // A new job would reset fetch_state and discard the failed or paused job's checkpoint.
        var latest = await LatestJobAsync(db, [MailboxFetchJob.JobType], ct);
        if (latest is { Status: JobStatus.Failed or JobStatus.Paused }
            && await jobs.ResumeAsync(latest.Id, ct) == JobActionResult.Ok)
        {
            return TypedResults.Ok(new StartFetchResponse(latest.Id));
        }

        if ((latest is null || !JobRow.Active.Contains(latest.Status)) && IsFetched(await ReadStateAsync(db, ct)))
        {
            return await EnqueueOrResumeIncrementalAsync(jobs, db, ct);
        }

        // A concurrent start that loses the insert gets the winner's job back with Created false.
        return await EnqueueAsync(jobs, MailboxFetchJob.JobType, MailboxFetchJob.Queue, ct);
    }

    /// <summary>
    /// 202 with a new job; 200 with the active one, or with the latest failed or paused one after resuming it; 409 when
    /// the local data belongs to another account, Gmail is not connected or the full mailbox fetch has not completed
    /// (there is no history ID to replay from).
    /// </summary>
    private static async Task<Results<Accepted<StartFetchResponse>, Ok<StartFetchResponse>, ProblemHttpResult>> StartIncrementalAsync(
        ITokenStore tokens, IJobService jobs, AppDbContext db, CancellationToken ct)
    {
        if (!await IsConnectedAsync(tokens, ct))
        {
            return GmailNotConnected("Connect Gmail in Setup before fetching the mailbox.");
        }

        if (!IsFetched(await ReadStateAsync(db, ct)))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Mailbox not fetched",
                detail: "The incremental fetch replays changes since the full fetch; run the mailbox fetch first.");
        }

        return await EnqueueOrResumeIncrementalAsync(jobs, db, ct);
    }

    /// <summary>The caller has checked the Gmail connection and that the mailbox is fetched.</summary>
    private static async Task<Results<Accepted<StartFetchResponse>, Ok<StartFetchResponse>, ProblemHttpResult>> EnqueueOrResumeIncrementalAsync(
        IJobService jobs, AppDbContext db, CancellationToken ct)
    {
        var latest = await LatestJobAsync(db, [IncrementalFetchJob.JobType], ct);
        if (latest is { Status: JobStatus.Failed or JobStatus.Paused }
            && await jobs.ResumeAsync(latest.Id, ct) == JobActionResult.Ok)
        {
            return TypedResults.Ok(new StartFetchResponse(latest.Id));
        }

        return await EnqueueAsync(jobs, IncrementalFetchJob.JobType, IncrementalFetchJob.Queue, ct);
    }

    private static async Task<Results<Accepted<StartFetchResponse>, Ok<StartFetchResponse>, ProblemHttpResult>> EnqueueAsync(
        IJobService jobs, string type, string queue, CancellationToken ct)
    {
        var (job, created) = await jobs.EnqueueAsync(type, queue, null, ct);
        var response = new StartFetchResponse(job.Id);
        return created ? TypedResults.Accepted($"/api/jobs/{job.Id}", response) : TypedResults.Ok(response);
    }

    /// <summary>
    /// 202 with a new job; 200 with the active job for the same target, or with the latest failed or paused one after
    /// resuming it from its cursor; 400 for an invalid target; 409 when Gmail is not connected. Jobs for different
    /// targets coexist (the dedup key is the target) and run one at a time on the fetch queue.
    /// </summary>
    private static async Task<Results<Accepted<StartFetchResponse>, Ok<StartFetchResponse>, ProblemHttpResult>> StartSenderAsync(
        SenderFetchRequest request, ITokenStore tokens, IJobService jobs, AppDbContext db, CancellationToken ct)
    {
        if (!SenderFetchTarget.TryParse(request.Target, out var cursor))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid sender",
                detail: "Enter a sender address (name@example.com) or a domain (example.com).");
        }

        if (!await IsConnectedAsync(tokens, ct))
        {
            return GmailNotConnected("Connect Gmail in Setup before fetching a sender.");
        }

        // A new job would start from page one and discard the failed or paused job's checkpoint.
        var latest = await db.Jobs.AsNoTracking()
            .Where(j => j.Type == SenderFetchJob.JobType && j.DedupKey == cursor.Target)
            .OrderByDescending(j => JobRow.Active.Contains(j.Status))
            .ThenByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (latest is { Status: JobStatus.Failed or JobStatus.Paused }
            && await jobs.ResumeAsync(latest.Id, ct) == JobActionResult.Ok)
        {
            return TypedResults.Ok(new StartFetchResponse(latest.Id));
        }

        // A concurrent start that loses the insert gets the winner's job back with Created false.
        var (job, created) = await jobs.EnqueueAsync(SenderFetchJob.JobType, SenderFetchJob.Queue, cursor, ct, dedupKey: cursor.Target);
        var response = new StartFetchResponse(job.Id);
        return created ? TypedResults.Accepted($"/api/jobs/{job.Id}", response) : TypedResults.Ok(response);
    }

    private static async Task<bool> IsConnectedAsync(ITokenStore tokens, CancellationToken ct) =>
        await tokens.GetAsync(ct) is { ReauthRequired: false };

    private static Task<FetchStateRow> ReadStateAsync(AppDbContext db, CancellationToken ct) =>
        db.FetchState.AsNoTracking().SingleAsync(s => s.Id == FetchStateRow.SingletonId, ct);

    private static bool IsFetched(FetchStateRow state) =>
        state.MailboxPhase == MailboxPhase.Completed && state.LastHistoryId is not null;

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
        var latest = await LatestJobAsync(db, [MailboxFetchJob.JobType, IncrementalFetchJob.JobType], ct);
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
            check.LocalAccountMasked,
            state.InboxTotal,
            state.AllMailTotal));
    }

    private static ProblemHttpResult GmailNotConnected(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Gmail not connected", detail: detail);

    /// <summary>
    /// The active job of <paramref name="types"/> (at most one per type, by the unique index; the newest if both are),
    /// else the most recent one.
    /// </summary>
    private static Task<JobRow?> LatestJobAsync(AppDbContext db, string[] types, CancellationToken ct) =>
        db.Jobs.AsNoTracking()
            .Where(j => types.Contains(j.Type))
            .OrderByDescending(j => JobRow.Active.Contains(j.Status))
            .ThenByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
}
