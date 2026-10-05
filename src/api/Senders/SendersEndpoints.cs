using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Senders;

public static class SendersEndpoints
{
    public static IEndpointRouteBuilder MapSendersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/senders").WithTags("Senders");
        group.MapGet("/", ListAsync);
        group.MapPut("/{address}/allowlist", SetAllowlistAsync);
        group.MapPost("/canonical/backfill", StartCanonicalBackfillAsync);
        group.MapPost("/stats/rebuild", StartStatsRebuildAsync);
        return endpoints;
    }

    /// <summary>Server-side paged, searchable and sortable senders; defaults to <c>sort=total&amp;dir=desc</c>.</summary>
    private static async Task<Results<Ok<PagedDto<SenderDto>>, ValidationProblem>> ListAsync(
        AppDbContext db,
        ISettingsStore settings,
        CancellationToken ct,
        string? search = null,
        int? page = null,
        int? pageSize = null,
        string? sort = null,
        string? dir = null,
        bool? allowlisted = null,
        string[]? kind = null)
    {
        var query = SenderQuery.Parse(search, page, pageSize, sort, dir, out var errors, kind);
        return query is null
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await (query with { Allowlisted = allowlisted }).ExecuteAsync(db, await DomainsAsync(settings, ct), ct));
    }

    /// <summary>
    /// Sets the sender's allowlist flag; <c>true</c> for an address never fetched creates a stub row, <c>false</c> for
    /// one is a 404.
    /// </summary>
    private static async Task<Results<Ok<SenderDto>, ValidationProblem, ProblemHttpResult>> SetAllowlistAsync(
        string address, AllowlistRequest request, SenderAllowlist allowlist, AppDbContext db, ISettingsStore settings, CancellationToken ct)
    {
        var normalised = SenderAllowlist.Normalise(address, out var addressError);
        Dictionary<string, string[]> errors = [];
        if (addressError is not null)
        {
            errors["address"] = [addressError];
        }

        if (request.Allowlisted is null)
        {
            errors["allowlisted"] = ["Required: true or false."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var sender = await allowlist.SetAsync(normalised!, request.Allowlisted!.Value, ct);
        return sender is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Sender not found")
            : TypedResults.Ok(await SenderQuery.ToDtoAsync(sender, await DomainsAsync(settings, ct), db, ct));
    }

    /// <summary>
    /// Queues a full sender stats rebuild, or returns the queued one not yet started (never 409); a running or paused
    /// rebuild gets a follow-up queued behind it.
    /// </summary>
    private static async Task<Accepted<JobDto>> StartStatsRebuildAsync(
        AppDbContext db, TimeProvider time, IJobService jobs, JobNotifier notifier, CancellationToken ct)
    {
        var id = await SenderStatsRebuildJob.EnqueueAsync(db, time, null, ct);
        await notifier.PublishAsync(db, id, ct);
        var job = await jobs.GetAsync(id, ct) ?? throw new InvalidOperationException($"Job {id} vanished after its enqueue.");
        return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
    }

    /// <summary>Queues the canonical sender backfill; 409 while one is queued, running or paused.</summary>
    private static async Task<Results<Accepted<JobDto>, ProblemHttpResult>> StartCanonicalBackfillAsync(IJobService jobs, CancellationToken ct)
    {
        var (job, created) = await jobs.EnqueueAsync(CanonicalBackfillJob.JobType, CanonicalBackfillJob.Queue, ct: ct);
        return created
            ? TypedResults.Accepted($"/api/jobs/{job.Id}", job)
            : TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Backfill in progress",
                detail: "A canonical sender backfill is already queued, running or paused.");
    }

    /// <summary>The allowlisted domains: the DTO reads its address flag from the row.</summary>
    private static async Task<IReadOnlyList<string>> DomainsAsync(ISettingsStore settings, CancellationToken ct) =>
        (await settings.GetAsync(ct)).Protection.AllowlistedDomains;
}
