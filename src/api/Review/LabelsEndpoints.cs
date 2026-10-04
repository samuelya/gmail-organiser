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
    private static Task<Results<Ok<IReadOnlyList<LabelDto>>, ProblemHttpResult>> ListAsync(LabelCatalog catalog, CancellationToken ct) =>
        ToResultAsync(catalog.GetAsync(ct));

    /// <summary>Drops the cache and reloads it from Gmail.</summary>
    private static Task<Results<Ok<IReadOnlyList<LabelDto>>, ProblemHttpResult>> RefreshAsync(LabelCatalog catalog, CancellationToken ct) =>
        ToResultAsync(catalog.RefreshAsync(ct));

    private static async Task<Results<Ok<IReadOnlyList<LabelDto>>, ProblemHttpResult>> ToResultAsync(Task<IReadOnlyList<GmailLabel>> load)
    {
        try
        {
            var labels = await load;
            return TypedResults.Ok<IReadOnlyList<LabelDto>>(
                [.. labels.Select(l => new LabelDto(l.Id, l.Name, SnakeCaseEnumConverter<GmailLabelType>.ToDb(l.Type)))]);
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailProblems.NotConnected(ex);
        }
    }
}
