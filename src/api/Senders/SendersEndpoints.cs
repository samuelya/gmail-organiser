using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Policies;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Senders;

public static class SendersEndpoints
{
    /// <summary>Bounds the <c>listId</c> query value; real List-Ids are far shorter.</summary>
    public const int MaxListIdLength = 500;

    /// <summary>Most canonical senders one Stage-0 request names.</summary>
    public const int MaxStage0Senders = 100;

    public static IEndpointRouteBuilder MapSendersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/senders").WithTags("Senders");
        group.MapGet("/", ListAsync);
        group.MapGet("/noisy", ListNoisyAsync);
        group.MapGet("/{address}/profile", GetProfileAsync);
        group.MapGet("/profile", GetListProfileAsync);
        group.MapPost("/noisy/proposals", ProposeAsync).RequireAccountMatch();
        group.MapPost("/archive", ArchiveAsync).RequireAccountMatch();
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

    /// <summary>Noisy senders by canonical address, highest volume first; defaults to <c>minMessages=10&amp;minUnreadRatio=0.9</c>.</summary>
    private static async Task<Results<Ok<PagedDto<NoisySenderDto>>, ValidationProblem>> ListNoisyAsync(
        AppDbContext db,
        ISettingsStore settings,
        TimeProvider time,
        CancellationToken ct,
        int? minMessages = null,
        double? minUnreadRatio = null,
        int? dormantDays = null,
        string? search = null,
        int? page = null,
        int? pageSize = null)
    {
        var query = NoisySenderQuery.Parse(minMessages, minUnreadRatio, dormantDays, search, page, pageSize, out var errors);
        return query is null
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await query.ExecuteAsync(db, await DomainsAsync(settings, ct), time, ct));
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

    /// <summary>The profile of a sender known by its raw or canonical address; 404 when unknown or without live mail.</summary>
    private static async Task<Results<Ok<SenderProfileDto>, ValidationProblem, ProblemHttpResult>> GetProfileAsync(
        string address, SenderProfileBuilder profiles, CancellationToken ct, bool includeBodies = false)
    {
        var normalised = SenderAllowlist.Normalise(address, out var error);
        if (error is not null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["address"] = [error] });
        }

        var canonical = await profiles.ResolveSenderAsync(normalised!, ct);
        return canonical is null
            ? SenderNotFound()
            : await ProfileAsync(profiles, PolicyScope.Sender, canonical, includeBodies, ct);
    }

    /// <summary>The profile of a mailing list by its <c>List-Id</c>; 404 when no live message carries it.</summary>
    private static async Task<Results<Ok<SenderProfileDto>, ValidationProblem, ProblemHttpResult>> GetListProfileAsync(
        SenderProfileBuilder profiles, CancellationToken ct, string? listId = null, bool includeBodies = false)
    {
        var key = GroupKey.NormaliseListId(listId);
        if (key is null || key.Length > MaxListIdLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["listId"] = [$"Required: a List-Id of at most {MaxListIdLength} characters."],
            });
        }

        return await ProfileAsync(profiles, PolicyScope.List, key, includeBodies, ct);
    }

    private static async Task<Results<Ok<SenderProfileDto>, ValidationProblem, ProblemHttpResult>> ProfileAsync(
        SenderProfileBuilder profiles, PolicyScope scope, string key, bool includeBodies, CancellationToken ct)
    {
        try
        {
            return await profiles.BuildAsync(scope, key, includeBodies, ct) is { } profile
                ? TypedResults.Ok(SenderProfileDto.From(profile))
                : SenderNotFound();
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailProblems.NotConnected(ex);
        }
    }

    private static ProblemHttpResult SenderNotFound() =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Sender not found");

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

    /// <summary>
    /// Creates pending Stage-0 suggestions for the senders' mail (#349); 422 naming a human, replied-to, allowlisted,
    /// not noisy or unknown sender (nothing created), 409 when an analysis stored a suggestion meanwhile, 503 without Gmail.
    /// </summary>
    private static async Task<Results<Ok<Stage0ProposalsResponse>, ValidationProblem, ProblemHttpResult>> ProposeAsync(
        Stage0ProposalsRequest request, Stage0Service stage0, CancellationToken ct)
    {
        var canonical = ParseSenders(request.CanonicalAddresses, out var errors);
        // A Stage-0 proposal always marks To-Be-Deleted; unsubscribe without deletion is the per-sender Unsubscribe (#350).
        if (request.ToBeDeleted != true)
        {
            errors["toBeDeleted"] = ["Must be true: a Stage-0 proposal always marks the mail To-Be-Deleted."];
        }

        if (canonical is null || errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        try
        {
            return ToResult(await stage0.ProposeAsync(canonical, request.Unsubscribe ?? false, ct), r => TypedResults.Ok(r));
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailProblems.NotConnected(ex);
        }
    }

    /// <summary>
    /// Queues the archive of the senders' unprotected inbox mail (#349): 202 with the job, whose History batch undoes it;
    /// 422 for a refused sender or nothing to archive, 503 without Gmail.
    /// </summary>
    private static async Task<Results<Accepted<JobDto>, ValidationProblem, ProblemHttpResult>> ArchiveAsync(
        SenderArchiveRequest request, Stage0Service stage0, CancellationToken ct)
    {
        if (ParseSenders(request.CanonicalAddresses, out var errors) is not { } canonical)
        {
            return TypedResults.ValidationProblem(errors);
        }

        try
        {
            return ToResult(await stage0.StartArchiveAsync(canonical, ct), j => TypedResults.Accepted($"/api/jobs/{j.Id}", j));
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailProblems.NotConnected(ex);
        }
    }

    /// <summary>1–<see cref="MaxStage0Senders"/> plain addresses, normalised and distinct; null with <paramref name="errors"/>.</summary>
    private static string[]? ParseSenders(string[]? addresses, out Dictionary<string, string[]> errors)
    {
        errors = [];
        if (addresses is not { Length: > 0 and <= MaxStage0Senders })
        {
            errors["canonicalAddresses"] = [$"Give between 1 and {MaxStage0Senders} canonical sender addresses."];
            return null;
        }

        var normalised = new List<string>(addresses.Length);
        foreach (var address in addresses)
        {
            if (SenderAllowlist.Normalise(address, out var error) is not { } value)
            {
                errors["canonicalAddresses"] = [error!];
                return null;
            }

            normalised.Add(value);
        }

        return [.. normalised.Distinct(StringComparer.Ordinal)];
    }

    private static Results<TOk, ValidationProblem, ProblemHttpResult> ToResult<T, TOk>(Stage0Result<T> result, Func<T, TOk> ok)
        where T : class
        where TOk : IResult =>
        result switch
        {
            { Value: { } value } => ok(value),
            { Conflict: true } => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Suggestions changed",
                detail: "An analysis stored suggestions for these senders meanwhile. Try again."),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Stage-0 action refused", detail: result.Refusal),
        };

    /// <summary>The allowlisted domains: the DTO reads its address flag from the row.</summary>
    private static async Task<IReadOnlyList<string>> DomainsAsync(ISettingsStore settings, CancellationToken ct) =>
        (await settings.GetAsync(ct)).Protection.AllowlistedDomains;
}
