using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
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
        bool? allowlisted = null)
    {
        var query = SenderQuery.Parse(search, page, pageSize, sort, dir, out var errors);
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

    /// <summary>The allowlisted domains only: the DTO reads its address flag from the row.</summary>
    private static async Task<Allowlist> DomainsAsync(ISettingsStore settings, CancellationToken ct) =>
        Allowlist.Empty with { Domains = (await settings.GetAsync(ct)).Protection.AllowlistedDomains };
}
