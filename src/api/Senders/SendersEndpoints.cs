using GmailOrganiser.Common;
using GmailOrganiser.Data;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Senders;

public static class SendersEndpoints
{
    public static IEndpointRouteBuilder MapSendersEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/senders").WithTags("Senders");
        group.MapGet("/", ListAsync);
        return endpoints;
    }

    /// <summary>Server-side paged, searchable and sortable senders; defaults to <c>sort=total&amp;dir=desc</c>.</summary>
    private static async Task<Results<Ok<PagedDto<SenderDto>>, ValidationProblem>> ListAsync(
        AppDbContext db,
        CancellationToken ct,
        string? search = null,
        int? page = null,
        int? pageSize = null,
        string? sort = null,
        string? dir = null)
    {
        var query = SenderQuery.Parse(search, page, pageSize, sort, dir, out var errors);
        return query is null
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await query.ExecuteAsync(db, ct));
    }
}
