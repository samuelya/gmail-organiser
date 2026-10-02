using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Review;

public static class LabelsEndpoints
{
    public static IEndpointRouteBuilder MapLabelsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/labels").WithTags("Labels");
        group.MapGet("/", ListAsync);
        group.MapPost("/refresh", RefreshAsync);
        return endpoints;
    }

    /// <summary>The Gmail labels from the 5-minute cache; 503 when Gmail is not connected.</summary>
    private static async Task<Results<Ok<IReadOnlyList<LabelDto>>, ProblemHttpResult>> ListAsync(LabelCatalog catalog, CancellationToken ct)
    {
        try
        {
            var labels = await catalog.GetAsync(ct);
            return TypedResults.Ok<IReadOnlyList<LabelDto>>(
                [.. labels.Select(l => new LabelDto(l.Id, l.Name, SnakeCaseEnumConverter<GmailLabelType>.ToDb(l.Type)))]);
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailNotConnected(ex);
        }
    }

    /// <summary>Drops the cache and reloads it.</summary>
    private static Task<Results<Ok<IReadOnlyList<LabelDto>>, ProblemHttpResult>> RefreshAsync(LabelCatalog catalog, CancellationToken ct)
    {
        catalog.Invalidate();
        return ListAsync(catalog, ct);
    }

    private static ProblemHttpResult GmailNotConnected(GmailNotConnectedException ex) =>
        TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Gmail not connected", detail: ex.Message);
}
