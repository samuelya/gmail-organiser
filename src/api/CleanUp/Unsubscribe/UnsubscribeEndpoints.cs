using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Gmail;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.CleanUp.Unsubscribe;

/// <summary>Per-sender unsubscribe under <c>/api/clean-up/senders/{address}/unsubscribe</c>.</summary>
public static class UnsubscribeEndpoints
{
    /// <summary>
    /// Registers the unsubscribe service, its in-flight set, the sender and its guarded, log-free HTTP client. With
    /// <see cref="GmailOptions.UseFake"/> the sender is <see cref="OfflineUnsubscribeSender"/>, chosen at resolution time.
    /// </summary>
    public static IServiceCollection AddUnsubscribe(this IServiceCollection services)
    {
        services.AddScoped<UnsubscribeService>();
        services.AddSingleton<UnsubscribeInFlight>();
        services.TryAddSingleton<HttpUnsubscribeSender>();
        services.TryAddSingleton<OfflineUnsubscribeSender>();
        services.TryAddSingleton<IUnsubscribeSender>(sp => sp.GetRequiredService<IOptions<GmailOptions>>().Value.UseFake
            ? sp.GetRequiredService<OfflineUnsubscribeSender>()
            : sp.GetRequiredService<HttpUnsubscribeSender>());
        // No HttpClientFactory loggers: they would log the full URL, which may carry a personal token.
        services.AddHttpClient(UnsubscribeHttp.ClientName, c => c.Timeout = UnsubscribeHttp.Timeout)
            .ConfigurePrimaryHttpMessageHandler(UnsubscribeHttp.CreateHandler)
            .RemoveAllLoggers();
        return services;
    }

    public static IEndpointRouteBuilder MapUnsubscribeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/clean-up/senders/{address}/unsubscribe").WithTags("Clean-up");
        group.MapGet("", GetAsync);
        group.MapPost("", SendAsync);
        group.MapPost("/mark", MarkAsync);
        return endpoints;
    }

    private static async Task<Results<Ok<UnsubscribeInfoDto>, NotFound, ValidationProblem, ProblemHttpResult>> GetAsync(
        string address, UnsubscribeService service, CancellationToken ct)
    {
        if (Normalise(address) is not { } sender)
        {
            return InvalidAddress();
        }

        try
        {
            return await service.GetInfoAsync(sender, ct) is { } info ? TypedResults.Ok(info) : TypedResults.NotFound();
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailNotConnected(ex);
        }
    }

    /// <summary>The one-click POST; 409 when the sender offers no one-click or a POST for it is already running.</summary>
    private static async Task<Results<Ok<UnsubscribeResultDto>, NotFound, ValidationProblem, ProblemHttpResult>> SendAsync(
        string address, UnsubscribeService service, CancellationToken ct)
    {
        if (Normalise(address) is not { } sender)
        {
            return InvalidAddress();
        }

        try
        {
            return await service.SendAsync(sender, ct) switch
            {
                (UnsubscribeOutcome.Ok, { } result) => TypedResults.Ok(result),
                (UnsubscribeOutcome.SenderNotFound, _) => TypedResults.NotFound(),
                (UnsubscribeOutcome.InProgress, _) => Conflict("Unsubscribe in progress", "A one-click unsubscribe for this sender is already running."),
                _ => Conflict("No one-click unsubscribe", "This sender offers no one-click unsubscribe; open the link or mailto address instead."),
            };
        }
        catch (GmailNotConnectedException ex)
        {
            return GmailNotConnected(ex);
        }
    }

    /// <summary>Records a link or <c>mailto:</c> the user opened by hand.</summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem>> MarkAsync(
        string address, MarkUnsubscribedRequest request, UnsubscribeService service, CancellationToken ct)
    {
        if (Normalise(address) is not { } sender)
        {
            return InvalidAddress();
        }

        if (request.Method is not (UnsubscribeMethod.Link or UnsubscribeMethod.Mailto))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["method"] = ["Must be link or mailto."] });
        }

        return await service.MarkAsync(sender, request.Method, ct) ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static string? Normalise(string address)
    {
        var value = address.Trim().ToLowerInvariant();
        return value.Length is > 0 and <= AnalysisPreviewEndpoint.MaxSenderAddressLength ? value : null;
    }

    private static ValidationProblem InvalidAddress() => TypedResults.ValidationProblem(new Dictionary<string, string[]>
    {
        ["address"] = [$"Must be 1 to {AnalysisPreviewEndpoint.MaxSenderAddressLength} characters."],
    });

    private static ProblemHttpResult Conflict(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);

    private static ProblemHttpResult GmailNotConnected(GmailNotConnectedException ex) =>
        TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Gmail not connected", detail: ex.Message);
}
